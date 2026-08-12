namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

public sealed class DedupeOptions
{
    // Content-hash algorithm. Defaults to SHA-256.
    public IContentHasher Hasher { get; init; } = Sha256ContentHasher.Instance;

    // Prefix under which content blobs are stored in the inner node. The inner node
    // is treated as a dedicated blob store - don't mix plain files under this prefix.
    public string BlobPrefix { get; init; } = ".blobs";

    // Directory fan-out: number of leading content-id chars used as a subfolder, e.g.
    // 2 → ".blobs/a1/a1b2c3…". Keeps any single directory from growing unbounded.
    // Set to 0 for a flat layout (handy with ReadableBlobNames).
    public int FanOut { get; init; } = 2;

    // When true, a new blob's storage key (ContentId) is the file name that first
    // stored the content - with a "-N" suffix if that name is already taken by
    // different content - so blobs on disk resemble real files instead of hashes.
    // Dedup still compares on the content hash, so identical content is stored once.
    public bool ReadableBlobNames { get; init; }
}
