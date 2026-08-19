namespace Dytools.VirtualFileSystem.Catalog;

// A decorator node's durable namespace: path -> content pointer + metadata.
// The single source of truth for what the node stores (the inner node only holds
// ContentId-keyed blobs), so it must outlive the process. Core ships a durable,
// zero-dependency implementation, JsonFileVfsCatalog; implement this over a database
// for scale.
//
// Paths are VfsPath, not string: implementations get the structured, normalized path
// (base + stream/ADS + query) without parsing. Consumers never touch this interface -
// they use IVirtualFileSystem with string paths.
//
// Reused by dedupe today and, by design, by future encryption / cache / hard-link
// nodes. Implementations must be safe for concurrent use.
public interface IVfsCatalog
{
    // -- Lookup / listing ------------------------------------------------------

    // One entry (file or directory), or null if nothing exists at path.
    ValueTask<CatalogEntry?> GetAsync(VfsPath path, CancellationToken ct = default);

    // Immediate children of a directory (non-recursive).
    IAsyncEnumerable<CatalogEntry> ListChildrenAsync(VfsPath path, CancellationToken ct = default);

    // How many file entries currently reference this content id. >0 means the blob is
    // still needed; 0 means it is orphaned and the node may delete it.
    ValueTask<int> ReferenceCountAsync(string contentId, CancellationToken ct = default);

    // The storage key already assigned to content with this hash, or null if unseen.
    // Lets a node dedup by hash while using a different storage key (ContentId).
    ValueTask<string?> FindContentIdByHashAsync(string hash, CancellationToken ct = default);

    // -- Mutations -------------------------------------------------------------

    // Create or replace an entry (a catalog holds no bytes - a file entry is a pointer + metadata, a
    // directory entry is just a node; IsDirectory picks which). Creates any missing ancestor dirs.
    // Returns the entry previously at that path, if any, so a file caller can GC the blob it
    // referenced; a directory upsert displaces nothing and returns null.
    ValueTask<CatalogEntry?> PutEntryAsync(CatalogEntry entry, CancellationToken ct = default);

    // Ensure a directory entry (and its ancestors) exist. No-op if already present.
    ValueTask EnsureDirectoryAsync(VfsPath path, DateTimeOffset timestamp, CancellationToken ct = default);

    // Remove a path; if it is a directory, remove the whole subtree. Yields every
    // removed FILE entry so the caller can GC now-orphaned blobs.
    IAsyncEnumerable<CatalogEntry> RemoveAsync(VfsPath path, CancellationToken ct = default);

    // Re-key a path (and its subtree, if a directory) to a new location. Content and
    // reference counts are unchanged - a pure namespace move.
    ValueTask MoveAsync(VfsPath fromPath, VfsPath toPath, CancellationToken ct = default);

    // -- Bulk mutations --------------------------------------------------------

    // Apply many entries (files and/or directories) as one unit. The default just loops PutEntryAsync
    // - correct, but a per-entry persist each time (and JsonFileVfsCatalog rewrites its whole document
    // per persist, so a large seed would be O(n²)). Implementations that can persist a whole set in one
    // shot (one document write, one DB transaction) override this.
    async ValueTask PutEntriesAsync(IEnumerable<CatalogEntry> entries, CancellationToken ct = default)
    {
        foreach (var e in entries) await PutEntryAsync(e, ct);
    }

    // Remove many paths/subtrees as one unit. Default loops the single-item RemoveAsync (draining the
    // removed-file streams, which bulk callers don't consume); override to persist once.
    async ValueTask RemoveAsync(IEnumerable<VfsPath> paths, CancellationToken ct = default)
    {
        foreach (var p in paths)
            await foreach (var _ in RemoveAsync(p, ct)) { }
    }

    // Best-effort: record that `path` was read at `accessedAt`, updating its AccessedAt. Approximate
    // by design - it only sees reads that go through the VFS, not external access - so it's meant for
    // "recently used", not audit. The default is a no-op, so catalogs that don't track access, or
    // track it in the backend, simply ignore it; JsonFileVfsCatalog implements it (coalesced, so a
    // read doesn't rewrite the document more than once per resolution window).
    ValueTask TouchAccessedAsync(VfsPath path, DateTimeOffset accessedAt, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

// Opt-in capability: a catalog that can hand out an isolated, independently reference-
// counted view of itself for a partition key. Implement this when one physical catalog
// backs several mounts - each mount uses ForPartition(mountKey) so their namespaces and
// GC never collide. Catalogs that don't implement it simply can't be shared that way.
public interface IPartitionedVfsCatalog : IVfsCatalog
{
    IVfsCatalog ForPartition(string partitionKey);
}
