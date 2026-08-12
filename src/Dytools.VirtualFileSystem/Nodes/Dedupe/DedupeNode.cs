using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

// Content-addressable, copy-on-write dedup decorator over any inner node.
//
// Bytes are stored once per unique content (keyed by hash) in the inner node under
// the blob prefix; an IVfsCatalog maps logical paths to those blobs and to metadata.
// Copy/Move are catalog-only (no byte movement). Identical content collapses to one
// blob; editing a path forks it to a new hash, leaving others untouched.
//
//   new DedupeNode(new LocalFsNode("/data"))              // in-memory catalog (tests)
//   new DedupeNode(new LocalFsNode("/data"), dbCatalog)   // durable catalog (production)
//
// The inner node is treated as a dedicated blob store - don't mix plain files into it.
public sealed class DedupeNode : VfsNodeBase
{
    private readonly IVfsNode      _inner;
    private readonly IVfsCatalog   _catalog;
    private readonly DedupeOptions _options;

    public DedupeNode(IVfsNode inner, IVfsCatalog? catalog = null, DedupeOptions? options = null)
    {
        _inner   = inner;
        _catalog = catalog ?? new InMemoryVfsCatalog();
        _options = options ?? new DedupeOptions();
    }

    // -- Read ------------------------------------------------------------------

    public override async Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var entry = await _catalog.GetAsync(PathOf(req), ct);
        if (entry is null || entry.IsDirectory || entry.ContentId is null) return null;
        return await _inner.OpenReadAsync(BlobReq(BlobPath(entry.ContentId)), ct);
    }

    // -- Write -----------------------------------------------------------------

    public override async Task<Stream> OpenWriteAsync(
        VfsNodeRequest req, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
    {
        var path     = PathOf(req);
        var existing = await _catalog.GetAsync(path, ct);

        if (mode == VfsWriteMode.CreateNew && existing is not null)
            throw new IOException($"VFS dedupe entry already exists: {path}");
        if (existing is { IsDirectory: true })
            throw new IOException($"Cannot open a directory for writing: {path}");

        var temp = CreateTemp();

        // Append re-materializes the existing content, then the caller writes on top.
        if (mode == VfsWriteMode.Append && existing?.ContentId is { } cid)
        {
            var src = await _inner.OpenReadAsync(BlobReq(BlobPath(cid)), ct);
            if (src is not null)
            {
                await using (src)
                    await src.CopyToAsync(temp, ct);
            }
        }

        var createdAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow;   // preserve creation time on overwrite
        return new DedupeWriteStream(this, path, temp, createdAt);
    }

    // Called by DedupeWriteStream on close: hash the buffered content, store the blob
    // once, record the file in the catalog, and GC the previously-referenced blob.
    internal async Task CommitWriteAsync(string path, FileStream temp, DateTimeOffset createdAt)
    {
        await temp.FlushAsync();
        var size = temp.Length;

        temp.Position = 0;
        var hash = await HashAsync(temp);

        // Dedup keys on the hash: reuse the storage key already assigned to this content.
        var contentId = await _catalog.FindContentIdByHashAsync(hash);
        if (contentId is null)
        {
            // New content - pick its storage key (the hash, or a readable file name),
            // then store the blob under it.
            contentId = _options.ReadableBlobNames ? await AllocateReadableIdAsync(path) : hash;
            if (!await _inner.ExistsAsync(BlobReq(BlobPath(contentId))))
            {
                temp.Position = 0;
                await using var w = await _inner.OpenWriteAsync(BlobReq(BlobPath(contentId)), VfsWriteMode.Create);
                await temp.CopyToAsync(w);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var prev = await _catalog.PutFileAsync(new CatalogEntry
        {
            Path        = path,
            IsDirectory = false,
            ContentId   = contentId,
            Hash        = hash,
            Size        = size,
            CreatedAt   = createdAt,
            ModifiedAt  = now,
        });

        // GC the blob the path used to reference, if nothing else points at it now.
        if (prev?.ContentId is { } old && old != contentId && await _catalog.ReferenceCountAsync(old) == 0)
            await _inner.DeleteAsync(BlobReq(BlobPath(old)));
    }

    // Derives a readable storage key from the file name, bumping "-N" until it is unique
    // among existing content ids. Called only for content not already stored.
    private async Task<string> AllocateReadableIdAsync(string path)
    {
        var leaf = LastSegment(path);
        if (string.IsNullOrEmpty(leaf)) leaf = "blob";

        var candidate = leaf;
        var seq = 1;
        while (await _catalog.ReferenceCountAsync(candidate) > 0)
            candidate = WithSequence(leaf, ++seq);
        return candidate;
    }

    // "report.pdf" + 2 → "report-2.pdf"; "report" + 2 → "report-2".
    private static string WithSequence(string name, int seq)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? $"{name}-{seq}" : $"{name[..dot]}-{seq}{name[dot..]}";
    }

    // -- Delete ----------------------------------------------------------------

    public override async Task DeleteAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        await foreach (var removed in _catalog.RemoveAsync(PathOf(req), ct))
        {
            if (removed.ContentId is { } id && await _catalog.ReferenceCountAsync(id, ct) == 0)
                await _inner.DeleteAsync(BlobReq(BlobPath(id)), ct);
        }
    }

    // -- Copy / Move (catalog-only) --------------------------------------------

    public override async Task CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        var from  = PathOf(src);
        var to    = PathOf(dst);
        var entry = await _catalog.GetAsync(from, ct)
                    ?? throw new FileNotFoundException($"VFS dedupe copy source not found: {from}");

        var now = DateTimeOffset.UtcNow;
        if (entry.IsDirectory) { await CopyTreeAsync(from, to, now, ct); return; }
        await _catalog.PutFileAsync(entry with { Path = to, CreatedAt = now, ModifiedAt = now }, ct);
    }

    public override Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
        => _catalog.MoveAsync(PathOf(src), PathOf(dst), ct).AsTask();

    private async Task CopyTreeAsync(string src, string dst, DateTimeOffset now, CancellationToken ct)
    {
        await _catalog.EnsureDirectoryAsync(dst, now, ct);
        await foreach (var child in _catalog.ListChildrenAsync(src, ct))
        {
            var childDst = dst + "/" + LastSegment(child.Path);
            if (child.IsDirectory) await CopyTreeAsync(child.Path, childDst, now, ct);
            else await _catalog.PutFileAsync(child with { Path = childDst, CreatedAt = now, ModifiedAt = now }, ct);
        }
    }

    // -- Metadata / listing ----------------------------------------------------

    public override async Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var entry = await _catalog.GetAsync(PathOf(req), ct);
        return entry is null ? null : ToNodeInfo(req.Path, entry);
    }

    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var child in _catalog.ListChildrenAsync(PathOf(req), ct))
            yield return ToNodeInfo(VfsPath.From(child.Path), child);
    }

    // Exposes the catalog so consumers can inspect it: vfs.GetCapability<IVfsCatalog>(path).
    public override T? GetCapability<T>() where T : class => _catalog as T ?? base.GetCapability<T>();

    // -- Internals -------------------------------------------------------------

    private static string PathOf(VfsNodeRequest req) => new(req.Path.PathSpan);

    private string BlobPath(string id)
        => _options.FanOut > 0 && id.Length > _options.FanOut
            ? $"{_options.BlobPrefix}/{id[.._options.FanOut]}/{id}"
            : $"{_options.BlobPrefix}/{id}";

    private static VfsNodeRequest BlobReq(string blobPath) => new(VfsPath.From(blobPath));

    private async Task<string> HashAsync(Stream content)
    {
        using var hash = _options.Hasher.Start();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int n;
            while ((n = await content.ReadAsync(buffer)) > 0)
                hash.Append(buffer.AsSpan(0, n));
            return hash.Complete();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static FileStream CreateTemp()
        => new(Path.Combine(Path.GetTempPath(), "vfs-dedupe-" + Guid.NewGuid().ToString("N")),
               FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 4096, useAsync: true);

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    private static VfsNodeInfo ToNodeInfo(VfsPath relativePath, CatalogEntry e)
    {
        var props = ImmutableDictionary<string, object>.Empty;
        if (e.ContentId is not null)   props = props.Add(VfsPropertyKeys.ContentId, e.ContentId);
        if (e.Hash is not null)        props = props.Add("ContentHash", e.Hash);
        if (e.ContentType is not null) props = props.Add("ContentType", e.ContentType);

        return new VfsNodeInfo
        {
            RelativePath = relativePath,
            IsFile       = !e.IsDirectory,
            IsDirectory  = e.IsDirectory,
            SizeBytes    = e.Size,
            CreatedAt    = e.CreatedAt,
            ModifiedAt   = e.ModifiedAt,
            Properties   = props,
        };
    }
}
