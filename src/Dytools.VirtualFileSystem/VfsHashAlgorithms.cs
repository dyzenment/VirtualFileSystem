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
    // Encoding matters as much as the algorithm: two nodes reporting the same md5 in different
    // encodings will never compare equal. Everything here is lowercase hex - what a dedupe node
    // already produces and what an S3 ETag already is - except QuickXor, which has no canonical hex
    // form and stays in the base64 Graph reports.

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

/// <summary>
/// Shared helpers for nodes implementing <see cref="IContentHashing"/>.
/// </summary>
public static class VfsHashing
{
    /// <summary>
    /// Hashes a stream with one of the standard algorithms, returning lowercase hex, or null when the
    /// algorithm is not one this can compute. Reads the whole stream.
    /// </summary>
    public static async Task<string?> ComputeAsync(
        Stream content, string algorithm, CancellationToken ct = default)
    {
        // QuickXor is base64 by convention, not hex, so it returns before the hex encoding below.
        if (Matches(algorithm, VfsHashAlgorithms.QuickXor))
            return await QuickXorHash.ComputeAsync(content, ct);

        byte[] digest;
        if (Matches(algorithm, VfsHashAlgorithms.Md5))
            digest = await System.Security.Cryptography.MD5.HashDataAsync(content, ct);
        else if (Matches(algorithm, VfsHashAlgorithms.Sha1))
            digest = await System.Security.Cryptography.SHA1.HashDataAsync(content, ct);
        else if (Matches(algorithm, VfsHashAlgorithms.Sha256))
            digest = await System.Security.Cryptography.SHA256.HashDataAsync(content, ct);
        else
            return null;

        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Algorithms <see cref="ComputeAsync"/> understands.</summary>
    public static IReadOnlyList<string> Computable { get; } =
        [VfsHashAlgorithms.Md5, VfsHashAlgorithms.Sha1, VfsHashAlgorithms.Sha256,
         VfsHashAlgorithms.QuickXor];

    /// <summary>Compares algorithm names the way the contract says to - case-insensitively.</summary>
    public static bool Matches(string requested, string algorithm)
        => string.Equals(requested, algorithm, StringComparison.OrdinalIgnoreCase);
}
