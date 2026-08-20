namespace Dytools.VirtualFileSystem.Catalog;

/// <summary>
/// One row in a decorator node's namespace: a file or directory. <see cref="ContentId"/> is an
/// opaque backing key - the node decides what it means (a content hash for a dedupe
/// node, an encrypted blob id for an encryption node, a backend key for a cache).
/// </summary>
public sealed record CatalogEntry
{
    /// <summary>
    /// Mount-relative path, carrying the full identity (base + stream/ADS + query). <c>""</c> is
    /// the root. <see cref="VfsPath"/> (not string) so implementations get the structured path without
    /// parsing and never lose the stream/query components.
    /// </summary>
    public required VfsPath        Path        { get; init; }

    /// <summary>Whether this entry is a directory (as opposed to a file).</summary>
    public required bool           IsDirectory { get; init; }

    /// <summary>
    /// Storage key for the bytes (the "inode"). Defaults to the content hash, but a node
    /// may use a friendlier key (e.g. the first file name that stored this content). Null for directories.
    /// </summary>
    public          string?        ContentId   { get; init; }   // null for directories

    /// <summary>
    /// Content hash - what dedup compares on. Equal to <see cref="ContentId"/> unless the node uses a
    /// separate readable storage key. Null for directories.
    /// </summary>
    public          string?        Hash        { get; init; }

    /// <summary>Size of the content in bytes. Null for directories.</summary>
    public          long?          Size        { get; init; }   // null for directories

    /// <summary>When the entry was created.</summary>
    public          DateTimeOffset CreatedAt   { get; init; }

    /// <summary>When the entry was last modified.</summary>
    public          DateTimeOffset ModifiedAt  { get; init; }

    /// <summary>When the entry was last accessed, or null when the backend doesn't track it.</summary>
    public          DateTimeOffset? AccessedAt { get; init; }   // null when the backend doesn't track it

    /// <summary>Optional MIME type of the content.</summary>
    public          string?        ContentType { get; init; }   // optional MIME type

    /// <summary>Whether the entry is hidden from listings.</summary>
    public          bool           IsHidden    { get; init; }

    /// <summary>
    /// Extension bag mirroring <c>VfsNodeInfo.Properties</c>: <c>string</c>→<c>string?</c> so it round-trips through
    /// any store (matches Azure/S3 metadata). Structured values are JSON-encoded into the string
    /// by the producer; use the typed accessors in <c>VfsPropertyExtensions</c> to read them.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Properties { get; init; }
}
