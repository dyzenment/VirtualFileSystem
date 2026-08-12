namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

// Pluggable content-hash algorithm for DedupeNode. Hashing is done client-side over
// the stream, so it is independent of the backing store (local disk, S3, Azure).
// Default is SHA-256; swap another algorithm in via DedupeOptions.Hasher.
public interface IContentHasher
{
    // Algorithm id, recorded for diagnostics / future migration (e.g. "sha256").
    string Algorithm { get; }

    // Begin an incremental hash so bytes can be fed as they are read.
    IContentHash Start();
}

public interface IContentHash : IDisposable
{
    void   Append(ReadOnlySpan<byte> data);

    // Finalize and return the content id (lowercase hex).
    string Complete();
}
