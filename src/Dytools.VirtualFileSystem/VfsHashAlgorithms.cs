namespace Dytools.VirtualFileSystem;

/// <summary>
/// Canonical names for content hash algorithms, as used by <c>IContentHashCapability</c> and the
/// <see cref="VfsPropertyKeys.HashPrefix"/> property keys.
/// <para>
/// Strings rather than an enum: which algorithms exist is a property of the backends, not of the VFS,
/// and a node is free to report one this list has never heard of. Compare case-insensitively.
/// </para>
/// </summary>
public static class VfsHashAlgorithms
{
    /// <summary>Microsoft's QuickXorHash - what SharePoint and OneDrive for Business report.</summary>
    public const string QuickXor = "quickxor";

    /// <summary>MD5. What Azure exposes as Content-MD5, and an S3 ETag for a single-part upload.</summary>
    public const string Md5 = "md5";

    /// <summary>SHA-1. Reported by personal OneDrive; not by SharePoint.</summary>
    public const string Sha1 = "sha1";

    /// <summary>SHA-256. The content identity a dedupe node stores.</summary>
    public const string Sha256 = "sha256";

    /// <summary>CRC-32. A checksum, not a content identity - equal values are weak evidence.</summary>
    public const string Crc32 = "crc32";
}
