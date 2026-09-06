namespace Dytools.VirtualFileSystem;

/// <summary>
/// Options for <c>OpenReadAsync</c>.
///
/// null options anywhere means <see cref="Default"/>: the node's own stream, whatever shape it is.
/// </summary>
/// <remarks>
/// Unlike the other operation options these never reach a node. Seekability is not something a
/// backend can do better than the layer above - an HTTP response body is forward-only however it is
/// asked for - so the VFS applies it to whatever stream the node returned, and
/// <see cref="IVfsNode"/> is unchanged. Middleware can still read it from <c>ctx.ReadOptions</c>.
/// </remarks>
public sealed record VfsReadOptions : VfsOperationOptions
{
    /// <summary>The defaults: the node's stream is returned as-is.</summary>
    public static readonly VfsReadOptions Default = new();

    /// <summary>
    /// Guarantees the returned stream reports <see cref="Stream.CanSeek"/>, wrapping it in a
    /// <see cref="SeekableReadStream"/> when the node's own stream is forward-only. A stream that is
    /// already seekable - anything from <c>LocalFsNode</c> - is returned untouched.
    /// <para>
    /// Reading stays lazy: the wrapper retains what has been read so a consumer can seek back into
    /// it, and never pulls more than the consumer asks for. The two exceptions are
    /// <see cref="Stream.Length"/> and a seek from <see cref="SeekOrigin.End"/>, which cannot be
    /// answered without reading the entry to its end - prefer <c>GetInfoAsync</c> for the size of a
    /// large remote entry.
    /// </para>
    /// </summary>
    public bool Seekable { get; init; }

    /// <summary>
    /// Bytes the <see cref="SeekableReadStream"/> retains in memory before spilling to a temp file
    /// that deletes itself when the stream is disposed. Ignored unless <see cref="Seekable"/> is set.
    /// </summary>
    public long MemoryThreshold { get; init; } = SeekableReadStream.DefaultMemoryThreshold;

    /// <summary>
    /// Options that guarantee a seekable stream, optionally overriding how much is held in memory
    /// before the buffer spills to disk.
    /// </summary>
    public static VfsReadOptions AsSeekable(long? memoryThreshold = null)
        => memoryThreshold is null
            ? new VfsReadOptions { Seekable = true }
            : new VfsReadOptions { Seekable = true, MemoryThreshold = memoryThreshold.Value };
}
