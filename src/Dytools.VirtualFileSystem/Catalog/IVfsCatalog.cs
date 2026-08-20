namespace Dytools.VirtualFileSystem.Catalog;

/// <summary>
/// A decorator node's durable namespace: path -&gt; content pointer + metadata.
/// The single source of truth for what the node stores (the inner node only holds
/// <c>ContentId</c>-keyed blobs), so it must outlive the process. Core ships a durable,
/// zero-dependency implementation, <see cref="JsonFileVfsCatalog"/>; implement this over a database
/// for scale.
/// </summary>
/// <remarks>
/// Paths are <see cref="VfsPath"/>, not string: implementations get the structured, normalized path
/// (base + stream/ADS + query) without parsing. Consumers never touch this interface -
/// they use <c>IVirtualFileSystem</c> with string paths.
/// <para>
/// Reused by dedupe today and, by design, by future encryption / cache / hard-link
/// nodes. Implementations must be safe for concurrent use.
/// </para>
/// </remarks>
public interface IVfsCatalog
{
    // -- Lookup / listing ------------------------------------------------------

    /// <summary>One entry (file or directory), or null if nothing exists at <paramref name="path"/>.</summary>
    ValueTask<CatalogEntry?> GetAsync(VfsPath path, CancellationToken ct = default);

    /// <summary>Immediate children of a directory (non-recursive).</summary>
    IAsyncEnumerable<CatalogEntry> ListChildrenAsync(VfsPath path, CancellationToken ct = default);

    /// <summary>
    /// How many file entries currently reference this content id. &gt;0 means the blob is
    /// still needed; 0 means it is orphaned and the node may delete it.
    /// </summary>
    ValueTask<int> ReferenceCountAsync(string contentId, CancellationToken ct = default);

    /// <summary>
    /// The storage key already assigned to content with this hash, or null if unseen.
    /// Lets a node dedup by hash while using a different storage key (<c>ContentId</c>).
    /// </summary>
    ValueTask<string?> FindContentIdByHashAsync(string hash, CancellationToken ct = default);

    // -- Mutations -------------------------------------------------------------

    /// <summary>
    /// Create or replace an entry (a catalog holds no bytes - a file entry is a pointer + metadata, a
    /// directory entry is just a node; <see cref="CatalogEntry.IsDirectory"/> picks which). Creates any missing ancestor dirs.
    /// </summary>
    /// <returns>
    /// The entry previously at that path, if any, so a file caller can GC the blob it
    /// referenced; a directory upsert displaces nothing and returns null.
    /// </returns>
    ValueTask<CatalogEntry?> PutEntryAsync(CatalogEntry entry, CancellationToken ct = default);

    /// <summary>Ensure a directory entry (and its ancestors) exist. No-op if already present.</summary>
    ValueTask EnsureDirectoryAsync(VfsPath path, DateTimeOffset timestamp, CancellationToken ct = default);

    /// <summary>
    /// Remove a path; if it is a directory, remove the whole subtree. Yields every
    /// removed FILE entry so the caller can GC now-orphaned blobs.
    /// </summary>
    IAsyncEnumerable<CatalogEntry> RemoveAsync(VfsPath path, CancellationToken ct = default);

    /// <summary>
    /// Re-key a path (and its subtree, if a directory) to a new location. Content and
    /// reference counts are unchanged - a pure namespace move.
    /// </summary>
    ValueTask MoveAsync(VfsPath fromPath, VfsPath toPath, CancellationToken ct = default);

    // -- Bulk mutations --------------------------------------------------------

    /// <summary>
    /// Apply many entries (files and/or directories) as one unit. The default just loops <see cref="PutEntryAsync"/>
    /// - correct, but a per-entry persist each time (and <see cref="JsonFileVfsCatalog"/> rewrites its whole document
    /// per persist, so a large seed would be O(n²)). Implementations that can persist a whole set in one
    /// shot (one document write, one DB transaction) override this.
    /// </summary>
    async ValueTask PutEntriesAsync(IEnumerable<CatalogEntry> entries, CancellationToken ct = default)
    {
        foreach (var e in entries) await PutEntryAsync(e, ct);
    }

    /// <summary>
    /// Remove many paths/subtrees as one unit. Default loops the single-item <see cref="RemoveAsync(VfsPath, CancellationToken)"/> (draining the
    /// removed-file streams, which bulk callers don't consume); override to persist once.
    /// </summary>
    async ValueTask RemoveAsync(IEnumerable<VfsPath> paths, CancellationToken ct = default)
    {
        foreach (var p in paths)
            await foreach (var _ in RemoveAsync(p, ct)) { }
    }

    /// <summary>
    /// Best-effort: record that <paramref name="path"/> was read at <paramref name="accessedAt"/>, updating its <c>AccessedAt</c>. Approximate
    /// by design - it only sees reads that go through the VFS, not external access - so it's meant for
    /// "recently used", not audit. The default is a no-op, so catalogs that don't track access, or
    /// track it in the backend, simply ignore it; <see cref="JsonFileVfsCatalog"/> implements it (coalesced, so a
    /// read doesn't rewrite the document more than once per resolution window).
    /// </summary>
    ValueTask TouchAccessedAsync(VfsPath path, DateTimeOffset accessedAt, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

/// <summary>
/// Opt-in capability: a catalog that can hand out an isolated, independently reference-
/// counted view of itself for a partition key. Implement this when one physical catalog
/// backs several mounts - each mount uses <see cref="ForPartition"/> so their namespaces and
/// GC never collide. Catalogs that don't implement it simply can't be shared that way.
/// </summary>
public interface IPartitionedVfsCatalog : IVfsCatalog
{
    /// <summary>Returns an isolated, independently reference-counted view of this catalog for the given partition key.</summary>
    IVfsCatalog ForPartition(string partitionKey);
}
