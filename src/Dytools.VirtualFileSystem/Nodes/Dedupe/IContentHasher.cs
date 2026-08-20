namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

/// <summary>
/// Pluggable content-hash algorithm for DedupeNode. Hashing is done client-side over
/// the stream, so it is independent of the backing store (local disk, S3, Azure).
/// Default is SHA-256; swap another algorithm in via <see cref="DedupeOptions.Hasher"/>.
/// </summary>
public interface IContentHasher
{
    /// <summary>Algorithm id, recorded for diagnostics / future migration (e.g. "sha256").</summary>
    string Algorithm { get; }

    /// <summary>Begin an incremental hash so bytes can be fed as they are read.</summary>
    IContentHash Start();
}

/// <summary>
/// An in-progress incremental content hash. Feed bytes with <see cref="Append"/> and finalize
/// with <see cref="Complete"/>.
/// </summary>
public interface IContentHash : IDisposable
{
    /// <summary>Appends a chunk of bytes to the running hash.</summary>
    /// <param name="data">The bytes to incorporate.</param>
    void   Append(ReadOnlySpan<byte> data);

    /// <summary>Finalize and return the content id (lowercase hex).</summary>
    string Complete();
}
