using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Azure;

// Mounts Azure Blob Storage as a VFS path, in one of two modes:
//
//   Fixed container  - new AzureBlobNode(containerClient, prefix?)
//                      the mount-relative path is the blob name, optionally prefixed.
//   Account-wide     - new AzureBlobNode(blobServiceClient)
//                      the FIRST path segment selects the container, the rest is the blob
//                      name. Mount "/azure" and address any container: /azure/<container>/<blob>.
//
// Azure SDK clients are immutable, thread-safe, and meant to be shared as singletons.
//
// Blob storage is a flat namespace with '/' separators, so "folders" are virtual -
// ListAsync uses GetBlobsByHierarchy with a '/' delimiter to surface them as directories
// (and, in account-wide mode, lists containers at the mount root).
//
// Native block-blob streams back OpenReadAsync/OpenWriteAsync directly. Append mode stages
// then rewrites a block blob. CopyAsync uses the base stream-copy fallback so it works
// under every auth mode.
public sealed class AzureBlobNode : VfsNodeBase, ICatalogMirror
{
    private readonly BlobServiceClient?   _service;    // account-wide mode
    private readonly BlobContainerClient? _container;  // fixed-container mode
    private readonly string               _prefix;     // fixed mode only; normalized, no leading/trailing '/'
    private readonly CatalogMirror?       _mirror;     // namespace cache; null = no caching

    private bool AccountWide => _container is null;

    public AzureBlobNode(BlobContainerClient container, string? pathPrefix = null, CatalogMirror? mirror = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _prefix    = pathPrefix?.Trim('/') ?? "";
        _mirror    = mirror;
    }

    public AzureBlobNode(BlobServiceClient service, CatalogMirror? mirror = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _prefix  = "";
        _mirror  = mirror;
    }

    // Activated by MountSingleton<AzureBlobNode> from the configured options + DI client.
    // No container in the options → account-wide mode.
    public AzureBlobNode(VfsMountOptions options, BlobServiceClient service, IServiceProvider services)
    {
        var o = options.Require<AzureBlobOptions>();
        if (string.IsNullOrWhiteSpace(o.Container))
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _prefix  = "";
        }
        else
        {
            _container = (service ?? throw new ArgumentNullException(nameof(service))).GetBlobContainerClient(o.Container);
            _prefix    = o.Prefix?.Trim('/') ?? "";
        }
        _mirror = CatalogMirror.FromOptions(o.UseCatalog, options, services);
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
            if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);   // reconcile a stale entry
            return null;
        }
    }

    public override async Task<Stream> OpenWriteAsync(
        VfsNodeRequest request, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
    {
        var (container, name) = Locate(Rel(request));
        if (container is null || name.Length == 0)
            throw new IOException("Cannot write to a container or the mount root - specify a blob path.");

        var inner = await OpenInnerWriteAsync(container, name, mode, ct);
        return WrapWrite(inner, request.Path);
    }

    private async Task<Stream> OpenInnerWriteAsync(
        BlobContainerClient container, string name, VfsWriteMode mode, CancellationToken ct)
    {
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

    public override async Task DeleteAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var (container, name) = Locate(Rel(request));
        if (container is null || name.Length == 0) return;   // not a blob
        await container.GetBlobClient(name)
                       .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
        if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);
    }

    // Wraps a write stream so the mirror records the blob once it's committed (on stream close).
    // Copy/Move flow through OpenWrite + Delete, so they're mirrored too.
    private Stream WrapWrite(Stream inner, VfsPath path)
    {
        if (_mirror is null) return inner;
        return new MirrorCommitStream(inner, async () =>
        {
            if (await GetInfoAsync(new VfsNodeRequest(path)) is { } info) await _mirror.UpsertAsync(info);
        });
    }

    // Blob names are case-sensitive, and a non-prefix pattern (e.g. "*.pdf") cannot be pushed down -
    // it forces a full container scan (unless a mirror serves the listing locally).
    protected override bool IsCaseSensitive => true;
    protected override bool RequiresFullScan(VfsListOptions options)
        => _mirror is null && !IsPurePrefixPattern(options.SearchPattern);

    // With a mirror: seed once, then serve every listing (incl. recursive) from the local catalog.
    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, VfsListOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_mirror is not null) await EnsureSeededAsync(ct);
        await foreach (var info in base.ListAsync(request, options ?? VfsListOptions.Default, ct))
            yield return info;
    }

    protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(
        VfsNodeRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_mirror is not null)
        {
            await foreach (var e in _mirror.ListChildrenAsync(request.Path, ct))
                yield return CatalogMirror.ToNodeInfo(e);
            yield break;
        }

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

    // -- Catalog mirror (ICatalogMirror) ---------------------------------------

    // Force a re-sync of the mirror against the store (picks up changes made outside this VFS).
    public Task RefreshAsync(CancellationToken ct = default) => ResyncAsync(ct);

    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (await _mirror!.GetStateAsync("seeded", ct) is null) await ResyncAsync(ct);
    }

    // Full re-list: clear the mirror, then a flat enumeration (fixed container, or every container
    // in account-wide mode), upserting each blob.
    private Task ResyncAsync(CancellationToken ct) => _mirror!.SyncAsync(async token =>
    {
        await _mirror.ClearAsync(token);

        if (AccountWide)
        {
            var svc = _service!;
            await foreach (var c in svc.GetBlobContainersAsync(cancellationToken: token))
            {
                var cont = svc.GetBlobContainerClient(c.Name);
                await foreach (var b in cont.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: null, token))
                    await _mirror.UpsertAsync(BlobInfo(Rebase(b.Name, c.Name), b), token);
            }
        }
        else
        {
            var scanPrefix = _prefix.Length == 0 ? null : _prefix + "/";
            await foreach (var b in _container!.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: scanPrefix, token))
            {
                var rel = Rebase(b.Name, "");
                if (rel.Length == 0) continue;
                await _mirror.UpsertAsync(BlobInfo(rel, b), token);
            }
        }

        await _mirror.SetStateAsync("seeded", "1", token);
    }, ct);

    private static VfsNodeInfo BlobInfo(string rel, BlobItem b) => new()
    {
        RelativePath = VfsPath.From(rel),
        IsFile       = true,
        IsDirectory  = false,
        SizeBytes    = b.Properties.ContentLength,
        ModifiedAt   = b.Properties.LastModified,
        CreatedAt    = b.Properties.CreatedOn,
        Properties   = BuildProps(b.Properties.ETag?.ToString(), b.Properties.ContentType),
    };

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

    private static IReadOnlyDictionary<string, string?> BuildProps(string? etag, string? contentType)
    {
        var props = ImmutableDictionary<string, string?>.Empty;
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

// Wraps a write stream so that, once the underlying blob write is committed (inner disposed),
// the node's callback records the result in the catalog mirror.
internal sealed class MirrorCommitStream(Stream inner, Func<Task> onClose) : Stream
{
    private bool _closed;

    public override bool CanWrite => true;
    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override long Length   => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override void      Write(byte[] buffer, int offset, int count)                        => inner.Write(buffer, offset, count);
    public override Task      WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    public override void      Flush()                                    => inner.Flush();
    public override Task      FlushAsync(CancellationToken ct)           => inner.FlushAsync(ct);
    public override int       Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long      Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
    public override void      SetLength(long value)                      => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseAsync().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    private async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        await inner.DisposeAsync().ConfigureAwait(false);   // commits the blob
        await onClose().ConfigureAwait(false);              // then record it in the mirror
    }
}
