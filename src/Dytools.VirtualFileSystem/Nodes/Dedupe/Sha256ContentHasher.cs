using System.Security.Cryptography;

namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

/// <summary>
/// Default content hasher: SHA-256, lowercase hex. Collision-resistant, standard,
/// and a 64-char key that is valid on every backend.
/// </summary>
public sealed class Sha256ContentHasher : IContentHasher
{
    /// <summary>Shared, stateless instance of the SHA-256 content hasher.</summary>
    public static readonly Sha256ContentHasher Instance = new();

    /// <summary>The algorithm id, <c>"sha256"</c>.</summary>
    public string Algorithm => "sha256";

    /// <summary>Begins a new incremental SHA-256 hash.</summary>
    public IContentHash Start() => new Sha256Hash();

    private sealed class Sha256Hash : IContentHash
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void   Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);
        public string Complete() => Convert.ToHexStringLower(_hash.GetHashAndReset());
        public void   Dispose()  => _hash.Dispose();
    }
}
