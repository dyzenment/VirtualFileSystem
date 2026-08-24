using System.Text.Json;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// The consumer-facing entry point for the virtual filesystem: stream operations, metadata,
/// listing, typed convenience helpers, scoping, and instance-level mounting.
/// </summary>
public interface IVirtualFileSystem : IAsyncDisposable
{
    /// <summary>The current working directory that relative paths resolve against, or null when unscoped.</summary>
    string? CurrentDirectory { get; }

    /// <summary>
    /// Returns a view scoped to <paramref name="path"/>. Relative paths resolve against
    /// <see cref="CurrentDirectory"/>; absolute paths (starting with <c>/</c>) always bypass the scope.
    /// </summary>
    IVirtualFileSystem ScopeTo(string path);

    /// <summary>
    /// Mounts a node local to this instance. Instance mounts are removed on <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// To mount globally (survive this instance), inject <see cref="IVfsMountRegistry"/> directly.
    /// </summary>
    void Mount(string mountPoint, IVfsNode node);

    /// <summary>Removes an instance-level mount at the given mount point.</summary>
    void Unmount(string mountPoint);

    /// <summary>Opens a readable stream for the entry, or null when it does not exist.</summary>
    Task<Stream?>   OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Opens a writable stream for the entry. A bare <see cref="VfsWriteMode"/> converts implicitly, so
    /// <c>OpenWriteAsync(path, VfsWriteMode.Append)</c> still binds; pass full
    /// <see cref="VfsWriteOptions"/> to also request timestamps on the written entry.
    /// </summary>
    Task<Stream>    OpenWriteAsync(string path, VfsWriteOptions? options = null, CancellationToken ct = default);

    /// <summary>Copies the entry at <paramref name="src"/> to <paramref name="dst"/>.</summary>
    Task            CopyAsync(string src, string dst, CancellationToken ct = default);

    /// <summary>Moves the entry at <paramref name="src"/> to <paramref name="dst"/>.</summary>
    Task            MoveAsync(string src, string dst, CancellationToken ct = default);

    /// <summary>Renames the entry at <paramref name="path"/> to <paramref name="newName"/> within the same parent.</summary>
    Task            RenameAsync(string path, string newName, CancellationToken ct = default);

    /// <summary>Deletes the entry at <paramref name="path"/>.</summary>
    Task            DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>Returns whether an entry exists at <paramref name="path"/>.</summary>
    Task<bool>      ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Returns metadata for the entry, or null when the path does not exist. The Path in the returned
    /// <see cref="VfsEntryInfo"/> is always the canonical VFS path with correct casing as reported by the node.
    /// </summary>
    Task<VfsEntryInfo?>            GetInfoAsync(string path, CancellationToken ct = default);

    /// <summary>Names only - lightweight enumeration of the directory's immediate children.</summary>
    IAsyncEnumerable<string>       ListAsync(string path, CancellationToken ct = default);

    /// <summary>Enumerates child names with options: recursion, search pattern, kind/hidden filtering, projection.</summary>
    IAsyncEnumerable<string>       ListAsync(string path, VfsListOptions options, CancellationToken ct = default);

    /// <summary>
    /// Full metadata per entry. The Path in each <see cref="VfsEntryInfo"/> is the full canonical VFS path.
    /// </summary>
    IAsyncEnumerable<VfsEntryInfo> ListInfoAsync(string path, CancellationToken ct = default);

    /// <summary>Enumerates entry metadata with options: recursion, search pattern, kind/hidden filtering, projection.</summary>
    IAsyncEnumerable<VfsEntryInfo> ListInfoAsync(string path, VfsListOptions options, CancellationToken ct = default);

    /// <summary>
    /// Serialises <paramref name="value"/> and writes it to <paramref name="path"/>. Default: JSON serialised
    /// over a stream. Nodes only ever see byte streams - no typed interface in core.
    /// </summary>
    Task    SendAsync<T>(string path, T value, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="SendAsync{T}(string,T,CancellationToken)"/>, with control over serialisation -
    /// naming policy, converters, indentation. Pass the same options to
    /// <see cref="RetrieveAsync{T}(string,JsonSerializerOptions,CancellationToken)"/> when reading back.
    /// </summary>
    Task    SendAsync<T>(string path, T value, JsonSerializerOptions jsonOptions, CancellationToken ct = default);

    /// <summary>Reads and deserialises the entry at <paramref name="path"/> into <typeparamref name="T"/>.</summary>
    Task<T?> RetrieveAsync<T>(string path, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="RetrieveAsync{T}(string,CancellationToken)"/>, with control over deserialisation.
    /// Must match the options the value was written with.
    /// </summary>
    Task<T?> RetrieveAsync<T>(string path, JsonSerializerOptions jsonOptions, CancellationToken ct = default);

    /// <summary>
    /// Resolves <paramref name="path"/> to its node, then calls <c>node.GetCapability&lt;T&gt;()</c>.
    /// Returns null if the node does not expose <typeparamref name="T"/>. The core never calls this -
    /// purely a consumer escape hatch.
    /// </summary>
    T? GetCapability<T>(string path) where T : class;
}
