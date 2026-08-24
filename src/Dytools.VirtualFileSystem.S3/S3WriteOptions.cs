namespace Dytools.VirtualFileSystem.Nodes.S3;

/// <summary>
/// S3-specific write options. Attach to <see cref="VfsWriteOptions.NodeOptions"/>.
/// </summary>
public sealed record S3WriteOptions : VfsNodeWriteOptions
{
    /// <summary>
    /// Whether S3 should compute and store a checksum for the object as it is written, overriding the
    /// node's own default.
    /// <para>
    /// Unlike an ETag this is a real content hash the service computes and keeps - and it holds for
    /// multipart uploads, where the ETag does not. Reading it back afterwards costs nothing extra, so
    /// it is the difference between a hash being free and having to download the object to get one.
    /// </para>
    /// </summary>
    public S3ChecksumRequest Checksum { get; init; } = S3ChecksumRequest.Inherit;
}

/// <summary>
/// What a single write asks of S3's checksum support. Three states rather than a nullable name,
/// because "say nothing, use the node's default" and "explicitly do not checksum this one" are
/// different intents and a null cannot mean both.
/// </summary>
public enum S3ChecksumRequest
{
    /// <summary>Use whatever the node was configured with.</summary>
    Inherit = 0,

    /// <summary>No checksum for this write, whatever the node's default says.</summary>
    None,

    /// <summary>CRC32 - cheapest, and a checksum rather than a content identity.</summary>
    Crc32,

    /// <summary>CRC32C - cheap, hardware-accelerated on most CPUs.</summary>
    Crc32C,

    /// <summary>SHA-1.</summary>
    Sha1,

    /// <summary>SHA-256 - the one worth choosing when the value is for comparing content.</summary>
    Sha256,
}
