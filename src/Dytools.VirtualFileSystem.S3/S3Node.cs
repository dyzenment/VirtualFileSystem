using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.S3;

// Mounts an Amazon S3 bucket (optionally rooted at a key prefix) as a VFS path.
//
// The node wraps a caller-supplied IAmazonS3 client. AWS SDK clients are thread-safe
// and intended to live for the lifetime of the application, so a single client shared
// across the app (registered as a singleton) is the recommended usage.
//
// Path mapping: the mount-relative VFS path becomes the S3 object key, optionally
// prefixed. S3 is a flat key space with '/' separators, so "folders" are virtual -
// ListAsync uses a '/' delimiter to surface them as directories.
//
// Native CopyAsync uses S3 CopyObject (server-side, no bytes through the client).
// Append is not supported - S3 objects are immutable and must be rewritten whole.
//
// Usage (register an IAmazonS3 in DI, then mount by bucket/prefix):
//   .MountSingleton<S3Node>("/archive", o => o.UseS3Bucket("my-bucket"))
//   .MountSingleton<S3Node>("/reports", o => o.UseS3Bucket("my-bucket/reports/2026"))
public sealed class S3Node : VfsNodeBase, ICatalogMirror
{
    private readonly IAmazonS3      _s3;
    private readonly string         _bucket;
    private readonly string         _prefix;   // normalized: no leading/trailing '/', "" when none
    private readonly CatalogMirror? _mirror;   // namespace cache; null = no caching

    public S3Node(IAmazonS3 client, string bucketName, string? keyPrefix = null, CatalogMirror? mirror = null)
    {
        _s3     = client ?? throw new ArgumentNullException(nameof(client));
        _bucket = string.IsNullOrWhiteSpace(bucketName)
            ? throw new ArgumentException("Bucket name is required.", nameof(bucketName))
            : bucketName;
        _prefix = keyPrefix?.Trim('/') ?? "";
        _mirror = mirror;
    }

    // Activated by MountSingleton<S3Node> from the configured options + DI client.
    public S3Node(VfsMountOptions options, IAmazonS3 client, IServiceProvider services)
        : this(client, options.Require<S3Options>().Bucket, options.Require<S3Options>().Prefix,
               ResolveMirror(options, services)) { }

    // Caching is opt-in: UseS3CachingCatalog stashes a CatalogSelection. Present = mirror the bucket
    // into the selected IVfsCatalog; absent = no caching.
    private static CatalogMirror? ResolveMirror(VfsMountOptions options, IServiceProvider services)
    {
        var sel = options.Get<CatalogSelection>();
        return sel is null ? null : new CatalogMirror(CatalogResolver.Resolve(services, sel.ServiceKey, sel.Partition));
    }

    public override async Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        try
        {
            var resp = await _s3.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = KeyFor(Rel(request)) }, ct);
            if (_mirror is not null) await _mirror.TouchAccessedAsync(request.Path, DateTimeOffset.UtcNow, ct);
            return resp.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);   // reconcile a stale entry
            return null;
        }
    }

    public override async Task<Stream> OpenWriteAsync(
        VfsNodeRequest request, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
    {
        if (mode == VfsWriteMode.Append)
            throw new NotSupportedException(
                "Amazon S3 objects are immutable and cannot be appended to. Rewrite the whole object instead.");

        var key = KeyFor(Rel(request));
        if (mode == VfsWriteMode.CreateNew && await ObjectExistsAsync(key, ct))
            throw new IOException($"S3 object already exists: s3://{_bucket}/{key}");

        // Write-through: on commit, record the object in the mirror.
        var path = request.Path;
        Func<long, Task>? onCommitted = _mirror is null
            ? null
            : size => _mirror.UpsertAsync(new VfsNodeInfo
            {
                RelativePath = path, IsFile = true, IsDirectory = false,
                SizeBytes = size, ModifiedAt = DateTimeOffset.UtcNow,
            });
        return new S3CommitStream(_s3, _bucket, key, onCommitted);
    }

    public override async Task DeleteAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = KeyFor(Rel(request)) }, ct);
        if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);
    }

    // Native server-side copy. Base MoveAsync/RenameAsync reuse this (copy + delete), so the mirror
    // tracks moves too (dst upserted here, src removed by DeleteAsync).
    public override async Task CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket      = _bucket, SourceKey      = KeyFor(Rel(src)),
            DestinationBucket = _bucket, DestinationKey = KeyFor(Rel(dst)),
        }, ct);
        if (_mirror is not null && await GetInfoAsync(dst, ct) is { } info) await _mirror.UpsertAsync(info, ct);
    }

    // S3 keys are case-sensitive, and a non-prefix pattern (e.g. "*.pdf") cannot be pushed down -
    // it forces a full bucket scan (unless a mirror serves the listing locally).
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

        var listPrefix = ListPrefixFor(Rel(request));
        string? token = null;
        do
        {
            var resp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket, Prefix = listPrefix, Delimiter = "/", ContinuationToken = token,
            }, ct);

            foreach (var commonPrefix in resp.CommonPrefixes ?? [])
                yield return new VfsNodeInfo
                {
                    RelativePath = VfsPath.From(StripPrefix(commonPrefix.TrimEnd('/'))),
                    IsFile       = false,
                    IsDirectory  = true,
                };

            foreach (var obj in resp.S3Objects ?? [])
            {
                if (obj.Key.EndsWith('/')) continue; // directory-marker object
                yield return new VfsNodeInfo
                {
                    RelativePath = VfsPath.From(StripPrefix(obj.Key)),
                    IsFile       = true,
                    IsDirectory  = false,
                    SizeBytes    = obj.Size,
                    ModifiedAt   = ToUtc(obj.LastModified),
                    Properties   = obj.ETag is null
                        ? ImmutableDictionary<string, string?>.Empty
                        : ImmutableDictionary<string, string?>.Empty.Add("ETag", obj.ETag),
                };
            }

            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        }
        while (token is not null);
    }

    public override async Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var rel = Rel(request);
        if (rel.Length == 0)                    // the mount root is always a directory
            return new VfsNodeInfo { RelativePath = request.Path, IsFile = false, IsDirectory = true };

        var key = KeyFor(rel);
        try
        {
            var meta  = await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct);
            var props = ImmutableDictionary<string, string?>.Empty;
            if (meta.ETag is not null)               props = props.Add("ETag", meta.ETag);
            if (meta.Headers?.ContentType is { } cty) props = props.Add("ContentType", cty);

            return new VfsNodeInfo
            {
                RelativePath = request.Path,
                IsFile       = true,
                IsDirectory  = false,
                SizeBytes    = meta.ContentLength,
                ModifiedAt   = ToUtc(meta.LastModified),
                Properties   = props,
            };
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Not an object - treat as a directory if any child keys exist under "key/".
            var resp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket, Prefix = key + "/", MaxKeys = 1,
            }, ct);

            var hasChildren = (resp.S3Objects?.Count ?? 0) > 0 || (resp.CommonPrefixes?.Count ?? 0) > 0;
            return hasChildren
                ? new VfsNodeInfo { RelativePath = request.Path, IsFile = false, IsDirectory = true }
                : null;
        }
    }

    // -- Catalog mirror (ICatalogMirror) ---------------------------------------

    // Force a re-sync of the mirror against the bucket (picks up changes made outside this VFS).
    public Task RefreshAsync(CancellationToken ct = default) => ResyncAsync(ct);

    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (await _mirror!.GetStateAsync("seeded", ct) is null) await ResyncAsync(ct);
    }

    // Cross-instance sync gate (re-up per S3 page means the TTL only has to cover one page).
    private static readonly TimeSpan SyncTtl           = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SyncAbortMargin   = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SyncTakeoverGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SyncPollInterval  = TimeSpan.FromSeconds(2);
    private const int SyncMaxAttempts = 3;

    private static string ExpiresValue(DateTimeOffset t) => t.ToUnixTimeMilliseconds().ToString();
    private static bool   PastGrace(string v, TimeSpan grace)
        => !long.TryParse(v, out var ms) || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ms + (long)grace.TotalMilliseconds;

    // Full re-list: clear the mirror, then a flat (delimiter-free) scan of the whole prefix. Each S3
    // page (up to 1000 objects) is applied as one bulk upsert - one document write per page, not per
    // object. A datetime lease in `sync-expires` gates it across instances: acquire atomically, re-up
    // the expiry each page, abort just before expiry if a page overruns; on success set "seeded" and
    // clear the lease. A loser waits for the holder to finish (cleared) or die (expired), taking over
    // in that case - bounded by SyncMaxAttempts, then it just serves what's mirrored.
    private async Task ResyncAsync(CancellationToken ct)
    {
        var mirror = _mirror!;
        for (var attempt = 0; attempt < SyncMaxAttempts; attempt++)
        {
            var expiresAt = DateTimeOffset.UtcNow + SyncTtl;
            if (await mirror.SetIfNullStateAsync("sync-expires", ExpiresValue(expiresAt), ct))
            {
                await mirror.ClearAsync(ct);

                var scanPrefix = _prefix.Length == 0 ? null : _prefix + "/";
                string? cont = null;
                do
                {
                    var resp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = _bucket, Prefix = scanPrefix, ContinuationToken = cont,
                    }, ct);

                    var batch = new List<VfsNodeInfo>();
                    foreach (var obj in resp.S3Objects ?? [])
                    {
                        if (obj.Key.EndsWith('/')) continue;              // directory-marker object
                        var rel = StripPrefix(obj.Key);
                        if (rel.Length == 0) continue;
                        batch.Add(new VfsNodeInfo
                        {
                            RelativePath = VfsPath.From(rel),
                            IsFile       = true,
                            IsDirectory  = false,
                            SizeBytes    = obj.Size,
                            ModifiedAt   = ToUtc(obj.LastModified),
                            Properties   = obj.ETag is null
                                ? ImmutableDictionary<string, string?>.Empty
                                : ImmutableDictionary<string, string?>.Empty.Add("ETag", obj.ETag),
                        });
                    }
                    if (batch.Count > 0) await mirror.UpsertAsync(batch, ct);
                    cont = resp.IsTruncated == true ? resp.NextContinuationToken : null;

                    if (DateTimeOffset.UtcNow >= expiresAt - SyncAbortMargin) return;   // overran → abort, let the lease lapse
                    expiresAt = DateTimeOffset.UtcNow + SyncTtl;
                    await mirror.SetStateAsync("sync-expires", ExpiresValue(expiresAt), ct);   // re-up
                }
                while (cont is not null);

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

    // -- Helpers ---------------------------------------------------------------

    private static string Rel(VfsNodeRequest request) => new(request.Path.PathSpan);

    private string KeyFor(string rel)
        => _prefix.Length == 0 ? rel : rel.Length == 0 ? _prefix : $"{_prefix}/{rel}";

    private string StripPrefix(string key)
        => _prefix.Length == 0 ? key : key.Length > _prefix.Length ? key[(_prefix.Length + 1)..] : "";

    private string ListPrefixFor(string relDir)
    {
        if (relDir.Length == 0) return _prefix.Length == 0 ? "" : _prefix + "/";
        return KeyFor(relDir) + "/";
    }

    private async Task<bool> ObjectExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static DateTimeOffset? ToUtc(DateTime? dt)
        => dt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc)) : null;
}

// Buffers written bytes to a temp file, then uploads to S3 once on dispose via
// TransferUtility (which switches to multipart automatically for large payloads).
// S3 has no streaming-append write, so we stage locally then upload atomically.
internal sealed class S3CommitStream : Stream
{
    private readonly IAmazonS3        _s3;
    private readonly string           _bucket;
    private readonly string           _key;
    private readonly string           _tempPath;
    private readonly FileStream       _temp;
    private readonly Func<long, Task>? _onCommitted;   // write-through the mirror (size in bytes)
    private          bool             _committed;

    public S3CommitStream(IAmazonS3 s3, string bucket, string key, Func<long, Task>? onCommitted = null)
    {
        _s3          = s3;
        _bucket      = bucket;
        _key         = key;
        _onCommitted = onCommitted;
        _tempPath    = Path.Combine(Path.GetTempPath(), "vfs-s3-" + Guid.NewGuid().ToString("N"));
        _temp        = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite,
                                      FileShare.None, bufferSize: 4096, useAsync: true);
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
        try
        {
            await _temp.FlushAsync().ConfigureAwait(false);
            var size = _temp.Length;
            _temp.Position = 0;
            var transfer = new TransferUtility(_s3);
            await transfer.UploadAsync(_temp, _bucket, _key).ConfigureAwait(false);
            if (_onCommitted is not null) await _onCommitted(size).ConfigureAwait(false);
        }
        finally
        {
            await _temp.DisposeAsync().ConfigureAwait(false);
            try { File.Delete(_tempPath); } catch { /* best-effort cleanup */ }
        }
    }
}
