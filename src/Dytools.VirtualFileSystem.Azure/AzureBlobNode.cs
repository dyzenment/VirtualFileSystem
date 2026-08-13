using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.Azure;

// Mounts Azure Blob Storage as a VFS path, in one of two modes:
//
//   Fixed container  — new AzureBlobNode(containerClient, prefix?)
//                      the mount-relative path is the blob name, optionally prefixed.
//   Account-wide     — new AzureBlobNode(blobServiceClient)
//                      the FIRST path segment selects the container, the rest is the blob
//                      name. Mount "/azure" and address any container: /azure/<container>/<blob>.
//
// Azure SDK clients are immutable, thread-safe, and meant to be shared as singletons.
//
// Blob storage is a flat namespace with '/' separators, so "folders" are virtual —
// ListAsync uses GetBlobsByHierarchy with a '/' delimiter to surface them as directories
// (and, in account-wide mode, lists containers at the mount root).
//
// Native block-blob streams back OpenReadAsync/OpenWriteAsync directly. Append mode stages
// then rewrites a block blob. CopyAsync uses the base stream-copy fallback so it works
// under every auth mode.
public sealed class AzureBlobNode : VfsNodeBase
{
    private readonly BlobServiceClient?   _service;    // account-wide mode
    private readonly BlobContainerClient? _container;  // fixed-container mode
    private readonly string               _prefix;     // fixed mode only; normalized, no leading/trailing '/'

    private bool AccountWide => _container is null;

    public AzureBlobNode(BlobContainerClient container, string? pathPrefix = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _prefix    = pathPrefix?.Trim('/') ?? "";
    }

    public AzureBlobNode(BlobServiceClient service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _prefix  = "";
    }

    public override async Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var (container, name) = Locate(Rel(request));
        if (container is null || name.Length == 0) return null;

        var blob = container.GetBlobClient(name);
        try
        {
            return await blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public override async Task<Stream> OpenWriteAsync(
        VfsNodeRequest request, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
    {
        var (container, name) = Locate(Rel(request));
        if (container is null || name.Length == 0)
            throw new IOException("Cannot write to a container or the mount root — specify a blob path.");

        if (mode == VfsWriteMode.Append)
        {
            // Azure blobs have a fixed type, and a block blob cannot be appended to in
            // place. To keep append consistent with Create (which writes block blobs),
            // stage any existing content into a temp buffer, let the caller append to it,
            // then rewrite the whole thing as a block blob on close.
            var target  = container.GetBlobClient(name);
            var staging = new FileStream(
                Path.Combine(Path.GetTempPath(), "vfs-az-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 4096, useAsync: true);
            try
            {
                var existing = await target.DownloadStreamingAsync(cancellationToken: ct);
                await existing.Value.Content.CopyToAsync(staging, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // No existing blob - a fresh append that starts from empty.
            }
            return new BlobRewriteStream(staging, target);
        }

        var block = container.GetBlockBlobClient(name);

        if (mode == VfsWriteMode.CreateNew)
        {
            var options = new BlockBlobOpenWriteOptions
            {
                OpenConditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            };
            try
            {
                return await block.OpenWriteAsync(overwrite: true, options, ct);
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                throw new IOException($"Azure blob already exists: {name}");
            }
        }

        return await block.OpenWriteAsync(overwrite: true, cancellationToken: ct);
    }

    public override Task DeleteAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var (container, name) = Locate(Rel(request));
        if (container is null || name.Length == 0) return Task.CompletedTask;   // not a blob
        return container.GetBlobClient(name)
                        .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var rel = Rel(request);

        // Account-wide mount root: the "directories" are the containers.
        if (AccountWide && rel.Length == 0)
        {
            await foreach (var c in _service!.GetBlobContainersAsync(cancellationToken: ct))
                yield return new VfsNodeInfo { RelativePath = VfsPath.From(c.Name), IsFile = false, IsDirectory = true };
            yield break;
        }

        var (container, sub) = Locate(rel);
        if (container is null) yield break;

        // Blob-name prefix to list under, and the segment to re-add to results (account-wide).
        var listPrefix       = AccountWide ? (sub.Length == 0 ? "" : sub + "/") : ListPrefixFor(rel);
        var containerSegment = AccountWide ? FirstSegment(rel) : "";

        await foreach (var item in container.GetBlobsByHierarchyAsync(
                           BlobTraits.None, BlobStates.None, delimiter: "/", prefix: listPrefix, cancellationToken: ct))
        {
            if (item.IsPrefix)
            {
                yield return new VfsNodeInfo
                {
                    RelativePath = VfsPath.From(Rebase(item.Prefix.TrimEnd('/'), containerSegment)),
                    IsFile       = false,
                    IsDirectory  = true,
                };
            }
            else
            {
                var b = item.Blob;
                yield return new VfsNodeInfo
                {
                    RelativePath = VfsPath.From(Rebase(b.Name, containerSegment)),
                    IsFile       = true,
                    IsDirectory  = false,
                    SizeBytes    = b.Properties.ContentLength,
                    ModifiedAt   = b.Properties.LastModified,
                    CreatedAt    = b.Properties.CreatedOn,
                    Properties   = BuildProps(b.Properties.ETag?.ToString(), b.Properties.ContentType),
                };
            }
        }
    }

    public override async Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var rel = Rel(request);
        if (rel.Length == 0)                    // the mount root is always a directory
            return new VfsNodeInfo { RelativePath = request.Path, IsFile = false, IsDirectory = true };

        var (container, name) = Locate(rel);
        if (container is null) return null;

        // Account-wide container root: a directory if the container exists.
        if (name.Length == 0)
            return (await container.ExistsAsync(ct)).Value
                ? new VfsNodeInfo { RelativePath = request.Path, IsFile = false, IsDirectory = true }
                : null;

        var blob = container.GetBlobClient(name);
        try
        {
            BlobProperties p = await blob.GetPropertiesAsync(cancellationToken: ct);
            return new VfsNodeInfo
            {
                RelativePath = request.Path,
                IsFile       = true,
                IsDirectory  = false,
                SizeBytes    = p.ContentLength,
                ModifiedAt   = p.LastModified,
                CreatedAt    = p.CreatedOn,
                Properties   = BuildProps(p.ETag.ToString(), p.ContentType),
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Not a blob - treat as a directory if any child blobs exist under "name/".
            await foreach (var _ in container.GetBlobsByHierarchyAsync(
                               BlobTraits.None, BlobStates.None, delimiter: "/", prefix: name + "/", cancellationToken: ct))
                return new VfsNodeInfo { RelativePath = request.Path, IsFile = false, IsDirectory = true };

            return null;
        }
    }

    // -- Helpers ---------------------------------------------------------------

    private static string Rel(VfsNodeRequest request) => new(request.Path.PathSpan);

    // Maps a mount-relative path to its container client and within-container blob name.
    // Fixed mode: the configured container, with the optional prefix applied.
    // Account-wide: the first path segment is the container, the rest is the blob name.
    // Container is null / name is "" when the path addresses the mount or a container root.
    private (BlobContainerClient? Container, string Name) Locate(string rel)
    {
        if (!AccountWide)
            return (_container, NameFor(rel));

        if (rel.Length == 0) return (null, "");                          // account root
        var slash = rel.IndexOf('/');
        return slash < 0
            ? (_service!.GetBlobContainerClient(rel), "")               // container root
            : (_service!.GetBlobContainerClient(rel[..slash]), rel[(slash + 1)..]);
    }

    private string NameFor(string rel)
        => _prefix.Length == 0 ? rel : rel.Length == 0 ? _prefix : $"{_prefix}/{rel}";

    // Converts a within-container blob name back to a mount-relative path.
    private string Rebase(string name, string containerSegment)
        => containerSegment.Length > 0
            ? $"{containerSegment}/{name}"                              // account-wide: prepend the container
            : _prefix.Length == 0 ? name : name.Length > _prefix.Length ? name[(_prefix.Length + 1)..] : "";

    private string ListPrefixFor(string relDir)
    {
        if (relDir.Length == 0) return _prefix.Length == 0 ? "" : _prefix + "/";
        return NameFor(relDir) + "/";
    }

    private static string FirstSegment(string rel)
    {
        var i = rel.IndexOf('/');
        return i < 0 ? rel : rel[..i];
    }

    private static IReadOnlyDictionary<string, object> BuildProps(string? etag, string? contentType)
    {
        var props = ImmutableDictionary<string, object>.Empty;
        if (!string.IsNullOrEmpty(etag))        props = props.Add("ETag", etag);
        if (!string.IsNullOrEmpty(contentType)) props = props.Add("ContentType", contentType);
        return props;
    }
}

// Buffers writes to a temp file (pre-seeded with existing content for append),
// then uploads the whole thing as a block blob on dispose. Azure cannot append
// to a block blob in place, so append is implemented as stage-then-rewrite.
internal sealed class BlobRewriteStream : Stream
{
    private readonly FileStream _temp;
    private readonly BlobClient _blob;
    private          bool       _committed;

    public BlobRewriteStream(FileStream temp, BlobClient blob)
    {
        _temp = temp;   // positioned at the end of any seeded existing content
        _blob = blob;
    }

    public override bool CanWrite => true;
    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override long Length   => _temp.Length;
    public override long Position { get => _temp.Position; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => _temp.Write(buffer, offset, count);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _temp.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => _temp.WriteAsync(buffer, ct);
    public override void Flush() => _temp.Flush();

    public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
    public override void SetLength(long value)                      => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await CommitAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CommitAsync().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    private async Task CommitAsync()
    {
        if (_committed) return;
        _committed = true;
        var path = _temp.Name;
        try
        {
            await _temp.FlushAsync().ConfigureAwait(false);
            _temp.Position = 0;
            await _blob.UploadAsync(_temp, overwrite: true).ConfigureAwait(false);
        }
        finally
        {
            await _temp.DisposeAsync().ConfigureAwait(false);
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
