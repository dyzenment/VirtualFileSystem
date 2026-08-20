namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

/// <summary>Configuration for a DedupeNode: hashing, blob layout, and naming.</summary>
public sealed class DedupeOptions
{
    /// <summary>Content-hash algorithm. Defaults to SHA-256.</summary>
    public IContentHasher Hasher { get; init; } = Sha256ContentHasher.Instance;

    /// <summary>
    /// Prefix under which content blobs are stored in the inner node. Default "": blobs live at the
    /// inner node's root (the node is treated as a dedicated blob store). Set a prefix to namespace
    /// them away from other content when you construct a DedupeNode directly over a shared node -
    /// when mounting via the builder, point UseSource at the desired path instead.
    /// </summary>
    public string BlobPrefix { get; init; } = "";

    /// <summary>
    /// Directory fan-out: number of leading content-id chars used as a subfolder, e.g.
    /// 2 → "a1/a1b2c3…". Keeps any single directory from growing unbounded.
    /// Set to 0 for a flat layout (handy with <see cref="ReadableBlobNames"/>).
    /// </summary>
    public int FanOut { get; init; } = 2;

    /// <summary>
    /// When true, a new blob's storage key (ContentId) is the file name that first
    /// stored the content - with a "-N" suffix if that name is already taken by
    /// different content - so blobs on disk resemble real files instead of hashes.
    /// Dedup still compares on the content hash, so identical content is stored once.
    /// </summary>
    public bool ReadableBlobNames { get; init; }
}
