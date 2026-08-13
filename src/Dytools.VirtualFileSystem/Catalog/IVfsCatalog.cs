namespace Dytools.VirtualFileSystem.Catalog;

// A decorator node's durable namespace: path -> content pointer + metadata.
// The single source of truth for what the node stores (the inner node only holds
// ContentId-keyed blobs), so it must outlive the process. Core ships a durable,
// zero-dependency implementation, JsonFileVfsCatalog; implement this over a database
// for scale.
//
// Paths are VfsPath, not string: implementations get the structured, normalized path
// (base + stream/ADS + query) without parsing. Consumers never touch this interface —
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

    // Create or replace a file entry, creating any missing ancestor directories.
    // Returns the entry previously at that path (if any) so the caller can GC the
    // blob it used to reference.
    ValueTask<CatalogEntry?> PutFileAsync(CatalogEntry file, CancellationToken ct = default);

    // Ensure a directory entry (and its ancestors) exist. No-op if already present.
    ValueTask EnsureDirectoryAsync(VfsPath path, DateTimeOffset timestamp, CancellationToken ct = default);

    // Remove a path; if it is a directory, remove the whole subtree. Yields every
    // removed FILE entry so the caller can GC now-orphaned blobs.
    IAsyncEnumerable<CatalogEntry> RemoveAsync(VfsPath path, CancellationToken ct = default);

    // Re-key a path (and its subtree, if a directory) to a new location. Content and
    // reference counts are unchanged — a pure namespace move.
    ValueTask MoveAsync(VfsPath fromPath, VfsPath toPath, CancellationToken ct = default);
}

// Opt-in capability: a catalog that can hand out an isolated, independently reference-
// counted view of itself for a partition key. Implement this when one physical catalog
// backs several mounts — each mount uses ForPartition(mountKey) so their namespaces and
// GC never collide. Catalogs that don't implement it simply can't be shared that way.
public interface IPartitionedVfsCatalog : IVfsCatalog
{
    IVfsCatalog ForPartition(string partitionKey);
}
