namespace Dytools.VirtualFileSystem;

/// <summary>
/// Immutable, stack-allocated. Passed to every <c>IVfsNode</c> method.
///
/// Path is the mount-relative <see cref="VfsPath"/> - a <see cref="VfsPath.WithOffset"/> slice of the full resolved path
/// that shares the same underlying string (zero allocation).
///   request.Path.PathSpan   → "folder/report.pdf"   (mount-relative base, no leading '/')
///   request.Path.StreamSpan → "thumbnail"            (ADS name)
///   request.Path.QuerySpan  → "width=200"            (query string)
///
/// Mount is the VFS mount prefix (e.g. "/local/c"). Stored so that the full path
/// can be reconstructed on demand for error messages:
///   <c>VfsPath.From(request.Mount, request.Path)</c>
/// Nodes must not use Mount for routing decisions - Path is always mount-relative.
///
/// CallContext is the same Dictionary instance as <see cref="VfsContext.Items"/> - a shared
/// per-call bag. Null when invoked outside the middleware pipeline.
/// </summary>
public readonly struct VfsNodeRequest
{
    /// <summary>Creates a request from a mount-relative path, its mount prefix, and the optional per-call context bag.</summary>
    /// <param name="relativePath">The mount-relative <see cref="VfsPath"/> for this request.</param>
    /// <param name="mount">The VFS mount prefix matched for this request.</param>
    /// <param name="callContext">Shared per-call context bag, or null outside the pipeline.</param>
    public VfsNodeRequest(VfsPath relativePath, VfsPath mount = default, IDictionary<string, object>? callContext = null)
    {
        Path        = relativePath;
        Mount       = mount;
        CallContext = callContext;
    }

    /// <summary>
    /// Mount-relative VfsPath for this request.
    /// <c>PathSpan</c> = "folder/report.pdf" (no leading '/', no stream or query).
    /// <c>StreamSpan</c> and <c>QuerySpan</c> remain accessible on this slice.
    /// Use this for all storage key lookups.
    /// </summary>
    public VfsPath Path { get; }

    /// <summary>
    /// The mount prefix matched for this request (e.g. "/local/c").
    /// Present only when the request was built through the pipeline.
    /// Reconstruct the full path via <c>VfsPath.From(request.Mount, request.Path)</c>
    /// - only do this on error paths.
    /// </summary>
    public VfsPath Mount { get; }

    /// <summary>
    /// Shared per-call context - same Dictionary instance as VfsContext.Items.
    /// Null when the node is called outside the middleware pipeline (e.g. in unit tests).
    /// </summary>
    public IDictionary<string, object>? CallContext { get; }
}
