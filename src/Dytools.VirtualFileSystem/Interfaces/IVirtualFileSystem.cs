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

    // -- Host filesystem interop -----------------------------------------------
    //
    // A file picker, a drag-and-drop, a command-line argument and Process.Start all deal in host
    // paths, and none of them will learn about mount points. These map between the two using what
    // the mounted nodes report through <see cref="ILocalPathMapping"/> - so the answer is whatever
    // the mounts actually say, and nothing here assumes a path shape or knows what a drive is.

    /// <summary>
    /// The best VFS path that reaches <paramref name="localPath"/>, or false when nothing mounted
    /// covers it - in which case the caller decides what to do (report it, or mount it and retry).
    /// <para>
    /// "Best" is the most specific mount covering the path, and never a path that would throw when
    /// used. Where several routes exist, <see cref="GetVfsPathCandidates"/> returns them all.
    /// </para>
    /// <para>
    /// <paramref name="localPath"/> must be fully qualified: a drive-rooted or UNC path on Windows, a
    /// "/"-rooted path elsewhere. Anything else throws rather than being completed from the process's
    /// working directory or current drive - a false here always means "no mount covers it", never
    /// "the input was not a place".
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="localPath"/> is empty or not fully qualified.</exception>
    bool TryGetVfsPath(string localPath, out string vfsPath);

    /// <summary>
    /// Every VFS path that reaches <paramref name="localPath"/>, most specific mount first, each
    /// labelled with the mount it routes through and the alias it goes via, if any. Empty when
    /// nothing covers it. Same input rule as <see cref="TryGetVfsPath"/>: fully qualified only.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="localPath"/> is empty or not fully qualified.</exception>
    IReadOnlyList<VfsPathCandidate> GetVfsPathCandidates(
        string localPath, VfsPathLookupOptions? options = null);

    /// <summary>
    /// Opens a readable stream for the entry, or null when it does not exist. Pass
    /// <see cref="VfsReadOptions.AsSeekable(long?)"/> to guarantee the result reports
    /// <see cref="Stream.CanSeek"/> - for a utility that will not accept a forward-only stream.
    /// <para>
    /// The options-free form is an extension on this method rather than a second member, so an
    /// implementation has one read to write and callers keep <c>OpenReadAsync(path)</c>.
    /// </para>
    /// </summary>
    Task<Stream?>   OpenReadAsync(string path, VfsReadOptions? options, CancellationToken ct = default);

    /// <summary>
    /// Opens a writable stream for the entry. A bare <see cref="VfsWriteMode"/> converts implicitly, so
    /// <c>OpenWriteAsync(path, VfsWriteMode.Append)</c> still binds; pass full
    /// <see cref="VfsWriteOptions"/> to also request timestamps on the written entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>You must dispose the returned stream, and the VFS will not do it for you.</b> For several
    /// backends the write does not happen when you call Write - it happens on dispose. A SharePoint,
    /// S3 or appending-Azure write stages your bytes to a local temp file and only uploads them when
    /// the stream closes, so an undisposed stream means nothing is stored at all and the temp file
    /// leaks. The same applies to anything <see cref="VfsWriteOptions"/> asked for: timestamps are
    /// stamped after the final flush, and a catalog or mirror row is written at the same point.
    /// </para>
    /// <para>
    /// Prefer <c>await using</c>. Plain <c>using</c> is safe - the synchronous path runs the commit
    /// through <see cref="VfsCommit.RunSync"/>, so it cannot deadlock against a synchronization
    /// context - but it blocks the calling thread for the whole upload and cannot be cancelled.
    /// Where the stream is handed to something that buffers - a <c>StreamWriter</c>, a serialiser -
    /// flush or dispose that first, since disposing the VFS stream is what commits, and anything
    /// still sitting in a writer's buffer will not have reached it.
    /// </para>
    /// </remarks>
    Task<Stream>    OpenWriteAsync(string path, VfsWriteOptions? options = null, CancellationToken ct = default);

    /// <summary>Copies the entry at <paramref name="src"/> to <paramref name="dst"/>.</summary>
    Task            CopyAsync(string src, string dst, CancellationToken ct = default);

    /// <summary>Moves the entry at <paramref name="src"/> to <paramref name="dst"/>.</summary>
    Task            MoveAsync(string src, string dst, CancellationToken ct = default);

    /// <summary>Renames the entry at <paramref name="path"/> to <paramref name="newName"/> within the same parent.</summary>
    Task            RenameAsync(string path, string newName, CancellationToken ct = default);

    /// <summary>Deletes the entry at <paramref name="path"/>, permanently.</summary>
    Task            DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Deletes the entry at <paramref name="path"/>. A bare <see cref="VfsDeleteDisposition"/>
    /// converts implicitly, so <c>DeleteAsync(path, VfsDeleteDisposition.Recycle)</c> binds.
    /// </summary>
    /// <remarks>
    /// <see cref="VfsDeleteDisposition.Recycle"/> throws <see cref="NotSupportedException"/> rather
    /// than falling back when the backend or volume has no recoverable delete - use
    /// <see cref="VfsDeleteDisposition.RecycleIfAvailable"/> where a permanent delete is an acceptable
    /// second best. Ask <c>GetEntryCapability&lt;IRecycling&gt;(path)</c> to know in advance.
    /// </remarks>
    Task            DeleteAsync(string path, VfsDeleteOptions? options, CancellationToken ct = default);

    /// <summary>Returns whether an entry exists at <paramref name="path"/>, following symlinks.</summary>
    Task<bool>      ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Returns whether an entry exists at <paramref name="path"/>. Pass
    /// <see cref="VfsMetadataOptions.NoFollow"/> to ask about a symlink itself, which is the only way
    /// to see a link whose target is missing.
    /// </summary>
    Task<bool>      ExistsAsync(string path, VfsMetadataOptions? options, CancellationToken ct = default);

    /// <summary>
    /// Returns metadata for the entry, or null when the path does not exist. The Path in the returned
    /// <see cref="VfsEntryInfo"/> is always the canonical VFS path with correct casing as reported by the node.
    /// </summary>
    Task<VfsEntryInfo?>            GetInfoAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Metadata for the entry, or null when the path does not exist. Pass
    /// <see cref="VfsMetadataOptions.NoFollow"/> to describe a symlink itself rather than its target -
    /// the <c>lstat</c> to the default's <c>stat</c>.
    /// </summary>
    Task<VfsEntryInfo?>            GetInfoAsync(string path, VfsMetadataOptions? options, CancellationToken ct = default);

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
    /// The entry-level capability <typeparamref name="T"/> for <paramref name="path"/>, already bound to
    /// that entry - so its methods take no path - or null when the node does not expose it. The core
    /// does not use this for ordinary operations; it is a consumer escape hatch, and the call itself
    /// does not run through middleware.
    /// </summary>
    T? GetEntryCapability<T>(string path) where T : class, IEntryCapability;

    /// <summary>
    /// The node-level capability <typeparamref name="T"/> for whichever node serves
    /// <paramref name="path"/>, or null when that node does not expose it. Any path under the mount
    /// will do - the capability belongs to the node, not the entry. Methods that address entries take
    /// absolute VFS paths. A consumer escape hatch; the call does not run through middleware.
    /// </summary>
    T? GetNodeCapability<T>(string path) where T : class, INodeCapability;
}
