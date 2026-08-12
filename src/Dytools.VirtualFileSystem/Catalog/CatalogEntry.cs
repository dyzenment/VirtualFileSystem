namespace Dytools.VirtualFileSystem.Catalog;

// One row in a decorator node's namespace: a file or directory. ContentId is an
// opaque backing key - the node decides what it means (a content hash for a dedupe
// node, an encrypted blob id for an encryption node, a backend key for a cache).
public sealed record CatalogEntry
{
    // Mount-relative, normalized path ('/'-separated, no leading '/'). "" is the root.
    public required string         Path        { get; init; }
    public required bool           IsDirectory { get; init; }

    // Storage key for the bytes (the "inode"). Defaults to the content hash, but a node
    // may use a friendlier key (e.g. the first file name that stored this content).
    public          string?        ContentId   { get; init; }   // null for directories

    // Content hash - what dedup compares on. Equal to ContentId unless the node uses a
    // separate readable storage key. Null for directories.
    public          string?        Hash        { get; init; }

    public          long?          Size        { get; init; }   // null for directories
    public          DateTimeOffset CreatedAt   { get; init; }
    public          DateTimeOffset ModifiedAt  { get; init; }
    public          string?        ContentType { get; init; }   // optional MIME type
}
