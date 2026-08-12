namespace Dytools.VirtualFileSystem.Catalog;

// A decorator node's durable namespace: path -> content pointer + metadata.
// The single source of truth for what the node stores (the inner node only holds
// ContentId-keyed blobs). Persist it over your database to survive restarts; an
// in-memory default (InMemoryVfsCatalog) ships for tests and non-durable use.
//
// Reused by dedupe today and, by design, by future encryption / cache / hard-link
// nodes. Implementations must be safe for concurrent use.
public interface IVfsCatalog
{
    // -- Lookup / listing ------------------------------------------------------

    // One entry (file or directory), or null if nothing exists at path.
    ValueTask<CatalogEntry?> GetAsync(string path, CancellationToken ct = default);

    // Immediate children of a directory (non-recursive).
    IAsyncEnumerable<CatalogEntry> ListChildrenAsync(string path, CancellationToken ct = default);

    // How many file entries currently reference this content id. >0 means the blob
    // is still needed; 0 means it is orphaned and the node may delete it.
    ValueTask<int> ReferenceCountAsync(string contentId, CancellationToken ct = default);

    // The ContentId (storage key) already assigned to content with this hash, or null
    // if the content is new. Lets a node dedupe by hash while storing under a separate
    // (e.g. human-readable) key.
    ValueTask<string?> FindContentIdByHashAsync(string hash, CancellationToken ct = default);

    // -- Mutations -------------------------------------------------------------

    // Create or replace a file entry, creating any missing ancestor directories.
    // Returns the entry previously at that path (if any) so the caller can GC the
    // blob it used to reference.
    ValueTask<CatalogEntry?> PutFileAsync(CatalogEntry file, CancellationToken ct = default);

    // Ensure a directory entry (and its ancestors) exist. No-op if already present.
    ValueTask EnsureDirectoryAsync(string path, DateTimeOffset timestamp, CancellationToken ct = default);

    // Remove a path; if it is a directory, remove the whole subtree. Yields every
    // removed FILE entry so the caller can GC now-orphaned blobs.
    IAsyncEnumerable<CatalogEntry> RemoveAsync(string path, CancellationToken ct = default);

    // Re-key a path (and its subtree, if a directory) to a new location. Content and
    // reference counts are unchanged - a pure namespace move.
    ValueTask MoveAsync(string fromPath, string toPath, CancellationToken ct = default);
}
