namespace Dytools.VirtualFileSystem;

public interface IVirtualFileSystem : IAsyncDisposable
{
    string? CurrentDirectory { get; }

    // ── Scoping ───────────────────────────────────────────────────────────────
    // Relative paths resolve against CurrentDirectory.
    // Absolute paths (starting with /) always bypass the scope.
    IVirtualFileSystem ScopeTo(string path);

    // ── Instance-level mounting ───────────────────────────────────────────────
    // These mounts are local to this instance and removed on DisposeAsync.
    // To mount globally (survive this instance), inject IVfsMountRegistry directly.
    void Mount(string mountPoint, IVfsNode node);
    void Unmount(string mountPoint);

    // ── Core stream operations ────────────────────────────────────────────────
    Task<Stream?>   OpenReadAsync(string path, CancellationToken ct = default);
    Task<Stream>    OpenWriteAsync(string path, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default);
    Task            CopyAsync(string src, string dst, CancellationToken ct = default);
    Task            MoveAsync(string src, string dst, CancellationToken ct = default);
    Task            RenameAsync(string path, string newName, CancellationToken ct = default);
    Task            DeleteAsync(string path, CancellationToken ct = default);
    Task<bool>      ExistsAsync(string path, CancellationToken ct = default);

    // ── Metadata ──────────────────────────────────────────────────────────────
    // GetInfoAsync: returns null when the path does not exist.
    // Path in the returned VfsEntryInfo is always the canonical VFS path
    // with correct casing as reported by the node.
    Task<VfsEntryInfo?>            GetInfoAsync(string path, CancellationToken ct = default);

    // ListAsync: names only - lightweight enumeration.
    IAsyncEnumerable<string>       ListAsync(string path, CancellationToken ct = default);

    // ListInfoAsync: full metadata per entry.
    // Path in each VfsEntryInfo is the full canonical VFS path.
    IAsyncEnumerable<VfsEntryInfo> ListInfoAsync(string path, CancellationToken ct = default);

    // ── Typed convenience sugar ───────────────────────────────────────────────
    // Default: JSON serialised over a stream.
    // Nodes only ever see byte streams - no typed interface in core.
    Task    SendAsync<T>(string path, T value, CancellationToken ct = default);
    Task<T?> RetrieveAsync<T>(string path, CancellationToken ct = default);

    // ── Consumer capability query ─────────────────────────────────────────────
    // Resolves path → node, then calls node.GetCapability<T>().
    // Returns null if the node does not expose T.
    // The core never calls this - purely a consumer escape hatch.
    T? GetCapability<T>(string path) where T : class;
}
