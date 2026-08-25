using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

/// <summary>
/// Content-addressable, copy-on-write dedup decorator over any inner node.
/// <para>
/// Bytes are stored once per unique content (keyed by hash) in the inner node under
/// the blob prefix; an <see cref="IContentAddressedCatalog"/> maps logical paths to those blobs and to
/// metadata, and answers which content is already stored.
/// Copy/Move are catalog-only (no byte movement). Identical content collapses to one
/// blob; editing a path forks it to a new hash, leaving others untouched.
/// </para>
/// <para>
/// The catalog is required - it is the durable source of truth for the namespace, so there
/// is deliberately no in-memory default. The inner node is a dedicated blob store; don't mix
/// plain files into it.
/// </para>
/// </summary>
/// <remarks>
/// <code>
///   var store = new LocalFsNode("/data");
///   new DedupeNode(store, new JsonFileVfsCatalog(store))   // durable, zero-dependency catalog
/// </code>
/// </remarks>
public sealed class DedupeNode : VfsNodeBase
{
    private readonly IVfsNode      _inner;
    private readonly IContentAddressedCatalog _catalog;
    private readonly DedupeOptions _options;

    /// <summary>Creates a dedup node over an <paramref name="inner"/> blob store and a required <paramref name="catalog"/>.</summary>
    /// <param name="inner">The backing node used as a dedicated blob store.</param>
    /// <param name="catalog">The durable source of truth mapping logical paths to blobs and metadata.</param>
    /// <param name="options">Hashing and blob-layout options; defaults are used when <c>null</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <c>null</c>.</exception>
    public DedupeNode(IVfsNode inner, IContentAddressedCatalog catalog, DedupeOptions? options = null)
    {
        _inner   = inner;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? new DedupeOptions();
    }

    /// <summary>
    /// Creates a dedup node from mount options. Activated by <c>MountSingleton&lt;DedupeNode&gt;</c>. Resolves the
    /// backing store (<c>UseSource</c>, via <c>NodeAt</c>), the catalog (default, or selected with
    /// <c>UseDedupeCatalog</c>), and the algorithm options - all from the configured mount options.
    /// </summary>
    public DedupeNode(VfsMountOptions options, IServiceProvider services)
        : this(ResolveInner(options, services), ResolveCatalog(options, services), ResolveAlgorithm(options)) { }

    private static IVfsNode ResolveInner(VfsMountOptions options, IServiceProvider sp)
    {
        var o = options.Require<DedupeMountOptions>();
        if (string.IsNullOrEmpty(o.Source))
            throw new InvalidOperationException("A dedupe mount requires a backing store; call UseSource(\"/path\").");
        return sp.NodeAt(o.Source);
    }

    // The catalog is required. UseDedupeCatalog is optional - it only picks a keyed/partitioned
    // catalog; without it the default registration is used.
    private static IContentAddressedCatalog ResolveCatalog(VfsMountOptions options, IServiceProvider sp)
    {
        var sel = options.Get<CatalogSelection>();
        var catalog = CatalogResolver.Resolve(sp, sel?.ServiceKey, sel?.Partition);

        // A dedupe mount needs the content index, not just the namespace. Saying so here beats a
        // null reference later when the first write tries to find whether the content is already
        // stored.
        return catalog as IContentAddressedCatalog
            ?? throw new InvalidOperationException(
                $"A dedupe mount requires a catalog implementing {nameof(IContentAddressedCatalog)}; " +
                $"the registered {catalog.GetType().Name} only provides the namespace operations.");
    }

    private static DedupeOptions ResolveAlgorithm(VfsMountOptions options)
    {
        var o = options.Require<DedupeMountOptions>();
        // Blobs live at the root of the UseSource path (BlobPrefix stays ""); nest them by pointing
        // UseSource at a subfolder instead of a separate prefix option.
        return new DedupeOptions
        {
            Hasher            = o.Hasher ?? Sha256ContentHasher.Instance,
            FanOut            = o.FanOut ?? 2,
            ReadableBlobNames = o.ReadableBlobNames,
        };
    }

    // -- Read ------------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var entry = await _catalog.GetAsync(req.Path, ct);
        if (entry is null || entry.IsDirectory || entry.ContentId is null) return null;
        await _catalog.TouchAccessedAsync(req.Path, DateTimeOffset.UtcNow, ct);   // catalog owns the file's metadata
        return await _inner.OpenReadAsync(BlobReq(BlobPath(entry.ContentId)), ct);
    }

    // -- Write -----------------------------------------------------------------

    /// <inheritdoc/>
    public override async Task<Stream> OpenWriteAsync(
        VfsNodeRequest req, VfsWriteOptions? options = null, CancellationToken ct = default)
    {
        options ??= VfsWriteOptions.Default;
        var mode = options.Mode;

        var path     = req.Path;
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

        var createdAt = options.CreatedAt ?? existing?.CreatedAt ?? DateTimeOffset.UtcNow;   // preserve creation time on overwrite
        return new DedupeWriteStream(this, path, temp, createdAt, options.ModifiedAt);
    }

    // Called by DedupeWriteStream on close: hash the buffered content, store the blob
    // once, record the file in the catalog, and GC the previously-referenced blob.
    internal async Task CommitWriteAsync(
        VfsPath path, FileStream temp, DateTimeOffset createdAt, DateTimeOffset? modifiedAt = null)
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

        // The timestamps live in the catalog, so a requested value is stored as-is - nothing to
        // reconcile against a backend clock.
        var now = modifiedAt ?? DateTimeOffset.UtcNow;
        var prev = await _catalog.PutEntryAsync(new CatalogEntry
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
            await _inner.DeleteAsync(BlobReq(BlobPath(old)), VfsDeleteOptions.Default);
    }

    // Derives a readable storage key from the file name, bumping "-N" until it is unique
    // among existing content ids. Called only for content not already stored.
    private async Task<string> AllocateReadableIdAsync(VfsPath path)
    {
        var leaf = path.GetName();
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

    /// <summary>
    /// Drops the path from the catalog and garbage-collects the blob behind it once nothing else
    /// references it.
    /// </summary>
    /// <remarks>
    /// Recycling is refused. A path here is a catalog row rather than a file, and the bytes it points
    /// at are usually still referenced by other paths - so there is nothing coherent to put in a bin
    /// and nothing coherent to restore. Recycle the inner node's blobs directly if that is what you
    /// want.
    /// </remarks>
    public override async Task DeleteAsync(
        VfsNodeRequest req, VfsDeleteOptions? options = null, CancellationToken ct = default)
    {
        (options ?? VfsDeleteOptions.Default).ResolveRecycle(available: false, req.Path);

        await foreach (var removed in _catalog.RemoveAsync(req.Path, ct))
        {
            if (removed.ContentId is { } id && await _catalog.ReferenceCountAsync(id, ct) == 0)
                await _inner.DeleteAsync(BlobReq(BlobPath(id)), VfsDeleteOptions.Default, ct);
        }
    }

    // -- Copy / Move (catalog-only) --------------------------------------------

    /// <inheritdoc/>
    public override async Task CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        var from  = src.Path;
        var to    = dst.Path;
        var entry = await _catalog.GetAsync(from, ct)
                    ?? throw new FileNotFoundException($"VFS dedupe copy source not found: {from}");

        var now = DateTimeOffset.UtcNow;
        if (entry.IsDirectory) { await CopyTreeAsync(from, to, now, ct); return; }
        await _catalog.PutEntryAsync(entry with { Path = to, CreatedAt = now, ModifiedAt = now }, ct);
    }

    /// <inheritdoc/>
    public override Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
        => _catalog.MoveAsync(src.Path, dst.Path, ct).AsTask();

    private async Task CopyTreeAsync(VfsPath src, VfsPath dst, DateTimeOffset now, CancellationToken ct)
    {
        await _catalog.EnsureDirectoryAsync(dst, now, ct);
        await foreach (var child in _catalog.ListChildrenAsync(src, ct))
        {
            var childDst = VfsPath.From(dst, child.Path.GetName());
            if (child.IsDirectory) await CopyTreeAsync(child.Path, childDst, now, ct);
            else await _catalog.PutEntryAsync(child with { Path = childDst, CreatedAt = now, ModifiedAt = now }, ct);
        }
    }

    // -- Metadata / listing ----------------------------------------------------

    /// <inheritdoc/>
    public override async Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var entry = await _catalog.GetAsync(req.Path, ct);
        return entry is null ? null : ToNodeInfo(req.Path, entry);
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(
        VfsNodeRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var child in _catalog.ListChildrenAsync(req.Path, ct))
            yield return ToNodeInfo(child.Path, child);
    }

    /// <summary>
    /// Exposes the catalog so consumers can inspect it:
    /// <c>vfs.GetNodeCapability&lt;IVfsCatalog&gt;(path)</c>. Otherwise defers to the base lookup.
    /// </summary>
    /// <typeparam name="T">The capability interface requested.</typeparam>
    /// <returns>The catalog when assignable to <typeparamref name="T"/>, otherwise the base lookup result, or <c>null</c>.</returns>
    public override T? GetNodeCapability<T>(VfsPath mountPoint) where T : class
        => _catalog as T ?? base.GetNodeCapability<T>(mountPoint);

    /// <summary>
    /// Hashing, bound to the entry asked for. The catalog already stores each entry's sha256 - it is
    /// the content identity deduplication keys on - so the common answer costs nothing.
    /// </summary>
    public override T? GetEntryCapability<T>(VfsPath relativePath) where T : class
        => new DedupeEntryHashing(this, relativePath) as T;

    // Serves DedupeEntryHashing; the bound object holds the path so the capability does not need one.
    internal async Task<string?> GetEntryHashAsync(
        VfsPath path, string algorithm, VfsHashBudget budget, CancellationToken ct)
    {
        var entry = await _catalog.GetAsync(path, ct);
        if (entry is null || entry.IsDirectory) return null;

        // The identity hash sits on the row, so the configured algorithm is free at any budget.
        if (VfsHashing.Matches(algorithm, VfsHashAlgorithms.Sha256)
            && entry.Hash is { Length: > 0 } stored)
            return stored;

        // Anything else has to be read, and a stored one is already in the row's properties.
        if (entry.Properties?.TryGetValue(VfsPropertyKeys.HashKey(algorithm), out var cached) == true
            && !string.IsNullOrEmpty(cached))
            return cached;

        if (budget < VfsHashBudget.Compute) return null;

        string? computed;
        await using (var content = await OpenReadAsync(new VfsNodeRequest(path), ct))
        {
            if (content is null) return null;
            computed = VfsHashing.Matches(algorithm, VfsHashAlgorithms.Sha256)
                ? await HashAsync(content)
                : await VfsHashing.ComputeAsync(content, algorithm, ct);
        }

        // The catalog is this node's own cache, so caching is not something to opt into. There is no
        // service behind a dedupe mount, so ComputeAndStore has nothing further to do.
        if (computed is not null)
            await StoreHashAsync(path, entry, algorithm, computed, ct);

        return computed;
    }

    // Parks a computed hash on the row. The row is the unit of invalidation: when the entry changes
    // the row is rewritten, and the hash goes with it rather than lingering as a wrong answer.
    private async Task StoreHashAsync(
        VfsPath path, CatalogEntry entry, string algorithm, string value, CancellationToken ct)
    {
        var props = entry.Properties is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(entry.Properties);
        props[VfsPropertyKeys.HashKey(algorithm)] = value;

        await _catalog.PutEntryAsync(entry with { Properties = props }, ct);
    }

    // -- Internals -------------------------------------------------------------

    private string BlobPath(string id)
    {
        if (string.IsNullOrEmpty(_options.BlobPrefix)) {
            return _options.FanOut > 0 && id.Length > _options.FanOut
                ? $"{id[.._options.FanOut]}/{id}"
                : id;    
        } else {
            return _options.FanOut > 0 && id.Length > _options.FanOut
                ? $"{_options.BlobPrefix}/{id[.._options.FanOut]}/{id}"
                : $"{_options.BlobPrefix}/{id}";
        }
    }

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

    private static VfsNodeInfo ToNodeInfo(VfsPath relativePath, CatalogEntry e)
    {
        // Start from the entry's own persisted bag, then overlay the well-known dedupe keys.
        var props = e.Properties is null
            ? ImmutableDictionary<string, string?>.Empty
            : ImmutableDictionary.CreateRange(e.Properties);
        if (e.ContentId is not null)   props = props.SetItem(VfsPropertyKeys.ContentId, e.ContentId);
        if (e.Hash is not null)        props = props.SetItem("ContentHash", e.Hash);
        if (e.ContentType is not null) props = props.SetItem("ContentType", e.ContentType);

        return new VfsNodeInfo
        {
            RelativePath = relativePath,
            IsFile       = !e.IsDirectory,
            IsDirectory  = e.IsDirectory,
            IsHidden     = e.IsHidden,
            SizeBytes    = e.Size,
            CreatedAt    = e.CreatedAt,
            ModifiedAt   = e.ModifiedAt,
            AccessedAt   = e.AccessedAt,
            Properties   = props,
        };
    }
}

/// <summary>
/// A <see cref="DedupeNode"/>'s hashing bound to one entry - what
/// <c>GetEntryCapability&lt;IContentHashing&gt;</c> hands back, so the interface needs no path.
/// </summary>
internal sealed class DedupeEntryHashing(DedupeNode node, VfsPath path) : IContentHashing
{
    /// <summary>SHA-256: the catalog stores it for every entry, being the identity dedup keys on.</summary>
    public IReadOnlyList<string> NativeAlgorithms { get; } = [VfsHashAlgorithms.Sha256];

    /// <summary>
    /// SHA-256 again - an entry whose row carries no hash can still be hashed by reading it back.
    /// Overlapping the native list is the point: only the node knows which of the two this entry
    /// costs, which is exactly what <c>withoutFetching</c> asks.
    /// </summary>
    public IReadOnlyList<string> ComputableAlgorithms { get; } = VfsHashing.Computable;

    /// <inheritdoc/>
    public Task<string?> GetHashAsync(
        string algorithm, VfsHashBudget budget = VfsHashBudget.Fetch, CancellationToken ct = default)
        => node.GetEntryHashAsync(path, algorithm, budget, ct);
}
