using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Azure;

/// <summary>
/// Mounts Azure Blob Storage as a VFS path, in one of two modes:
/// <list type="bullet">
///   <item><description>
///     Fixed container - <c>new AzureBlobNode(containerClient, prefix?)</c>:
///     the mount-relative path is the blob name, optionally prefixed.
///   </description></item>
///   <item><description>
///     Account-wide - <c>new AzureBlobNode(blobServiceClient)</c>:
///     the FIRST path segment selects the container, the rest is the blob
///     name. Mount <c>"/azure"</c> and address any container: <c>/azure/&lt;container&gt;/&lt;blob&gt;</c>.
///   </description></item>
/// </list>
/// <para>Azure SDK clients are immutable, thread-safe, and meant to be shared as singletons.</para>
/// <para>
/// Blob storage is a flat namespace with '/' separators, so "folders" are virtual -
/// <see cref="ListAsync"/> uses <c>GetBlobsByHierarchy</c> with a '/' delimiter to surface them as directories
/// (and, in account-wide mode, lists containers at the mount root).
/// </para>
/// <para>
/// Native block-blob streams back <see cref="OpenReadAsync"/>/<see cref="OpenWriteAsync"/> directly. Append mode stages
/// then rewrites a block blob. <c>CopyAsync</c> uses the base stream-copy fallback so it works
/// under every auth mode.
/// </para>
/// </summary>
public sealed class AzureBlobNode : VfsNodeBase, ICatalogMirror
{
    private readonly BlobServiceClient?   _service;    // account-wide mode
    private readonly BlobContainerClient? _container;  // fixed-container mode
    private readonly string               _prefix;     // fixed mode only; normalized, no leading/trailing '/'
    private readonly CatalogMirror?       _mirror;     // namespace cache; null = no caching

    private bool AccountWide => _container is null;

    /// <summary>Creates a node in fixed-container mode over the given container, optionally rooted at a path prefix.</summary>
    /// <param name="container">The container client to mount.</param>
    /// <param name="pathPrefix">Optional path prefix the mount is rooted at; leading/trailing slashes are trimmed.</param>
    /// <param name="mirror">Optional namespace cache; <c>null</c> disables caching.</param>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
    public AzureBlobNode(BlobContainerClient container, string? pathPrefix = null, CatalogMirror? mirror = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _prefix    = pathPrefix?.Trim('/') ?? "";
        _mirror    = mirror;
    }

    /// <summary>Creates a node in account-wide mode: the first path segment selects the container, the rest is the blob name.</summary>
    /// <param name="service">The blob service client to mount.</param>
    /// <param name="mirror">Optional namespace cache; <c>null</c> disables caching.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <c>null</c>.</exception>
    public AzureBlobNode(BlobServiceClient service, CatalogMirror? mirror = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _prefix  = "";
        _mirror  = mirror;
    }

    /// <summary>
    /// Activated by <c>MountSingleton&lt;AzureBlobNode&gt;</c> from the configured options + DI client.
    /// No container in the options means account-wide mode.
    /// </summary>
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
        _mirror = ResolveMirror(options, services);
    }

    // Caching is opt-in: UseAzureCachingCatalog stashes a CatalogSelection. Present = mirror the
    // container/account into the selected IVfsCatalog; absent = no caching.
    private static CatalogMirror? ResolveMirror(VfsMountOptions options, IServiceProvider services)
    {
        var sel = options.Get<CatalogSelection>();
        return sel is null ? null : new CatalogMirror(CatalogResolver.Resolve(services, sel.ServiceKey, sel.Partition));
    }

    /// <summary>Opens the blob for reading, or returns <c>null</c> if it does not exist (or the path is not a blob).</summary>
    /// <returns>A readable stream over the blob's contents, or <c>null</c> when the blob is not found.</returns>
    public override async Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var (container, name) = Locate(Rel(request));
        if (container is null || name.Length == 0) return null;

        var blob = container.GetBlobClient(name);
        try
        {
            var stream = await blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct);
            if (_mirror is not null) await _mirror.TouchAccessedAsync(request.Path, DateTimeOffset.UtcNow, ct);
            return stream;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);   // reconcile a stale entry
            return null;
        }
    }

    /// <summary>
    /// Opens a write stream for the blob. <see cref="VfsWriteMode.Append"/> stages existing content then rewrites
    /// the whole block blob on close; other modes stream directly to a block blob.
    /// </summary>
    /// <returns>A writable stream that commits the blob when disposed.</returns>
    /// <exception cref="IOException">
    /// The path addresses a container or the mount root rather than a blob, or <paramref name="mode"/> is
    /// <see cref="VfsWriteMode.CreateNew"/> and the blob already exists.
    /// </exception>
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

    /// <summary>Deletes the blob (including snapshots) and removes it from the mirror. No-op when the path is not a blob.</summary>
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

    /// <summary>Blob names are case-sensitive.</summary>
    protected override bool IsCaseSensitive => true;

    /// <summary>
    /// A non-prefix pattern (e.g. <c>"*.pdf"</c>) cannot be pushed down - it forces a full container
    /// scan (unless a mirror serves the listing locally).
    /// </summary>
    protected override bool RequiresFullScan(VfsListOptions options)
        => _mirror is null && !IsPurePrefixPattern(options.SearchPattern);

    /// <summary>With a mirror: seed once, then serve every listing (incl. recursive) from the local catalog.</summary>
    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, VfsListOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_mirror is not null) await EnsureSeededAsync(ct);
        await foreach (var info in base.ListAsync(request, options ?? VfsListOptions.Default, ct))
            yield return info;
    }

    /// <summary>
    /// Lists the immediate children of a directory: from the mirror when present, the account's containers at
    /// an account-wide mount root, otherwise a hierarchical blob listing that surfaces prefixes as directories.
    /// </summary>
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

    /// <summary>
    /// Returns metadata for the blob at the path, a directory entry for an existing container or when child
    /// blobs exist under it, or <c>null</c> when nothing matches. The mount root is always reported as a directory.
    /// </summary>
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

    /// <summary>Force a re-sync of the mirror against the store (picks up changes made outside this VFS).</summary>
    public Task RefreshAsync(CancellationToken ct = default) => ResyncAsync(ct);

    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (await _mirror!.GetStateAsync("seeded", ct) is null) await ResyncAsync(ct);
    }

    private const int ResyncChunk = 2000;   // blobs per bulk upsert during a re-list

    // Cross-instance sync gate (re-up per chunk means the TTL only has to cover one chunk).
    private static readonly TimeSpan SyncTtl           = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SyncAbortMargin   = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SyncTakeoverGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SyncPollInterval  = TimeSpan.FromSeconds(2);
    private const int SyncMaxAttempts = 3;

    private static string ExpiresValue(DateTimeOffset t) => t.ToUnixTimeMilliseconds().ToString();
    private static bool   PastGrace(string v, TimeSpan grace)
        => !long.TryParse(v, out var ms) || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ms + (long)grace.TotalMilliseconds;

    // Full re-list: clear the mirror, then a flat enumeration (fixed container, or every container in
    // account-wide mode). Blobs are buffered and applied in bulk chunks - one document write per chunk.
    // A datetime lease in `sync-expires` gates it across instances: acquire atomically, re-up the expiry
    // each chunk, abort just before expiry if a chunk overruns; on success set "seeded" and clear the
    // lease. A loser waits for the holder to finish (cleared) or die (expired), taking over in that case
    // - bounded by SyncMaxAttempts, then it just serves what's mirrored.
    private async Task ResyncAsync(CancellationToken ct)
    {
        var mirror = _mirror!;
        for (var attempt = 0; attempt < SyncMaxAttempts; attempt++)
        {
            var expiresAt = DateTimeOffset.UtcNow + SyncTtl;
            if (await mirror.SetIfNullStateAsync("sync-expires", ExpiresValue(expiresAt), ct))
            {
                await mirror.ClearAsync(ct);

                var batch = new List<VfsNodeInfo>(ResyncChunk);
                // Upsert the buffered chunk and re-up the lease; returns true if we overran and should abort.
                async Task<bool> FlushAsync()
                {
                    if (batch.Count == 0) return false;
                    await mirror.UpsertAsync(batch, ct);
                    batch.Clear();
                    if (DateTimeOffset.UtcNow >= expiresAt - SyncAbortMargin) return true;
                    expiresAt = DateTimeOffset.UtcNow + SyncTtl;
                    await mirror.SetStateAsync("sync-expires", ExpiresValue(expiresAt), ct);
                    return false;
                }

                if (AccountWide)
                {
                    var svc = _service!;
                    await foreach (var c in svc.GetBlobContainersAsync(cancellationToken: ct))
                    {
                        var cont = svc.GetBlobContainerClient(c.Name);
                        await foreach (var b in cont.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: null, ct))
                        {
                            batch.Add(BlobInfo(Rebase(b.Name, c.Name), b));
                            if (batch.Count >= ResyncChunk && await FlushAsync()) return;   // overran → abort
                        }
                    }
                }
                else
                {
                    var scanPrefix = _prefix.Length == 0 ? null : _prefix + "/";
                    await foreach (var b in _container!.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: scanPrefix, ct))
                    {
                        var rel = Rebase(b.Name, "");
                        if (rel.Length == 0) continue;
                        batch.Add(BlobInfo(rel, b));
                        if (batch.Count >= ResyncChunk && await FlushAsync()) return;   // overran → abort
                    }
                }

                await FlushAsync();
                await mirror.SetStateAsync("seeded", "1", ct);
                await mirror.ClearStateAsync("sync-expires", CancellationToken.None);   // done → waiters serve at once
                return;
            }

            while (true)
            {
                await Task.Delay(SyncPollInterval, ct);
                var v = await mirror.GetStateAsync("sync-expires", ct);
                if (v is null) return;                                                   // finished → serve current mirror
                if (PastGrace(v, SyncTakeoverGrace)) { await mirror.ClearStateAsync("sync-expires", ct); break; }   // died → take over
            }
        }
    }

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
