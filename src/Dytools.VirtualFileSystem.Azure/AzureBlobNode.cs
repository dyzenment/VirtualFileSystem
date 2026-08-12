using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.Azure;

// Mounts an Azure Blob Storage container (optionally rooted at a path prefix) as a VFS path.
//
// The node wraps a caller-supplied BlobContainerClient. Azure SDK clients are immutable
// and thread-safe, and are intended to be shared as singletons for the application's
// lifetime - that is the recommended usage here.
//
// Path mapping: the mount-relative VFS path becomes the blob name, optionally prefixed.
// Blob storage is a flat namespace with '/' separators, so "folders" are virtual -
// ListAsync uses GetBlobsByHierarchy with a '/' delimiter to surface them as directories.
//
// Native block-blob streams back OpenReadAsync/OpenWriteAsync directly. Append mode uses
// an append blob. CopyAsync intentionally uses the base stream-copy fallback (read then
// write through the authenticated client) so it works under every auth mode - server-side
// StartCopyFromUri would require a SAS or public source under OAuth/managed-identity auth.
//
// Usage:
//   .Mount("/team", sp => new AzureBlobNode(
//       sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("docs")))
//   .MountAzureBlob("/team", "docs")                 // convenience extension
//   .MountAzureBlob("/reports", "docs", "reports")   // rooted at a path prefix
public sealed class AzureBlobNode : VfsNodeBase
{
    private readonly BlobContainerClient _container;
    private readonly string              _prefix;   // normalized: no leading/trailing '/', "" when none

    public AzureBlobNode(BlobContainerClient container, string? pathPrefix = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _prefix    = pathPrefix?.Trim('/') ?? "";
    }

    public override async Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(NameFor(Rel(request)));
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
        var name = NameFor(Rel(request));

        if (mode == VfsWriteMode.Append)
        {
            // Azure blobs have a fixed type, and a block blob cannot be appended to in
            // place. To keep append consistent with Create (which writes block blobs),
            // stage any existing content into a temp buffer, let the caller append to it,
            // then rewrite the whole thing as a block blob on close.
            var target  = _container.GetBlobClient(name);
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

        var block = _container.GetBlockBlobClient(name);

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
        => _container.GetBlobClient(NameFor(Rel(request)))
                     .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);

    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var listPrefix = ListPrefixFor(Rel(request));

        await foreach (var item in _container.GetBlobsByHierarchyAsync(
                           BlobTraits.None, BlobStates.None, delimiter: "/", prefix: listPrefix, cancellationToken: ct))
        {
            if (item.IsPrefix)
            {
                yield return new VfsNodeInfo
                {
                    RelativePath = VfsPath.From(StripPrefix(item.Prefix.TrimEnd('/'))),
                    IsFile       = false,
                    IsDirectory  = true,
                };
            }
            else
            {
                var b = item.Blob;
                yield return new VfsNodeInfo
                {
                    RelativePath = VfsPath.From(StripPrefix(b.Name)),
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

        var blob = _container.GetBlobClient(NameFor(rel));
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
            await foreach (var _ in _container.GetBlobsByHierarchyAsync(
                               BlobTraits.None, BlobStates.None, delimiter: "/", prefix: NameFor(rel) + "/", cancellationToken: ct))
                return new VfsNodeInfo { RelativePath = request.Path, IsFile = false, IsDirectory = true };

            return null;
        }
    }

    // -- Helpers ---------------------------------------------------------------

    private static string Rel(VfsNodeRequest request) => new(request.Path.PathSpan);

    private string NameFor(string rel)
        => _prefix.Length == 0 ? rel : rel.Length == 0 ? _prefix : $"{_prefix}/{rel}";

    private string StripPrefix(string name)
        => _prefix.Length == 0 ? name : name.Length > _prefix.Length ? name[(_prefix.Length + 1)..] : "";

    private string ListPrefixFor(string relDir)
    {
        if (relDir.Length == 0) return _prefix.Length == 0 ? "" : _prefix + "/";
        return NameFor(relDir) + "/";
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
