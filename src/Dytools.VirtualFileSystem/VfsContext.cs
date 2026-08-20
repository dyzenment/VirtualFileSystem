using Dytools.VirtualFileSystem.Internal;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Built once per VFS call before the pipeline runs.
/// Passed through every middleware in registration order.
///
/// Path is changed only via <see cref="Reroute"/>.
/// <see cref="Reroute"/> atomically re-resolves <see cref="ResolvedNode"/>, <see cref="MountPoint"/>, and <see cref="ResolvedPath"/>
/// so context is always internally consistent.
///
/// <see cref="Items"/> is a shared bag for middleware-to-middleware communication within
/// a single call (same pattern as <c>HttpContext.Items</c>). Use typed extension
/// methods to avoid stringly-typed key access. See <c>VfsContextKeys</c> for
/// well-known keys used by built-in middleware.
///
/// <see cref="Options"/> holds per-operation parameters (WriteMode, future flags) in a
/// packed long - no heap allocation. New parameters never require changing
/// IVfsMiddleware signatures.
///
/// Layout (80 bytes on 64-bit .NET):
///   object header:  16B
///   _registry:       8B
///   ResolvedNode:    8B
///   _items:          8B  (SmallBag, lazy - null in common case)
///   _resolved:      16B  (VfsPath)
///   MountPoint:     16B  (VfsPath)
///   Options:         8B  (VfsCallOptions, long-backed)
/// </summary>
public sealed class VfsContext
{
    private readonly IVfsMountRegistry _registry;
    private readonly IServiceProvider? _ambient;   // scope used to resolve scoped/transient nodes

    internal VfsContext(VfsPath path, IVfsMountRegistry registry, IServiceProvider? ambient = null)
    {
        _registry = registry;
        _ambient  = ambient;
        Reroute(path);
    }

    private VfsPath _resolved;
    private SmallBag? _items;

    /// <summary>
    /// The current (possibly rewritten by middleware) VFS path.
    /// Always in sync with <see cref="ResolvedNode"/> - change only via <see cref="Reroute"/>.
    ///
    /// Non-aliased (common): <c>_resolved</c> directly - zero alloc.
    /// Aliased (rare): reads origin string from the items bag - the bag is already
    /// allocated when an alias is followed (AliasFollowed flag is also stored there).
    /// </summary>
    public VfsPath Path
        => _items is not null && _items.TryGetValue(VfsContextKeys.AliasOrigin, out var origin)
            ? VfsPath.From((string)origin)
            : _resolved;

    /// <summary>
    /// Path after VFS-level alias expansion (read-only).
    /// Differs from <see cref="Path"/> when an <c>Alias()</c> entry was followed.
    /// </summary>
    public VfsPath ResolvedPath => _resolved;

    /// <summary>
    /// The mount point prefix that was matched (e.g. "/local/c").
    /// Sourced directly from the registry key - no allocation.
    /// </summary>
    public VfsPath MountPoint { get; private set; }

    /// <summary>The node responsible for this path. Always in sync with <see cref="Path"/>.</summary>
    public IVfsNode ResolvedNode { get; private set; } = default!;

    /// <summary>
    /// Per-operation options: WriteMode, and future flags (CopyOverwrite, etc.).
    /// Stored as a packed long - no heap allocation. Set by <c>VfsPipeline</c> before
    /// the chain runs; readable and writable by middleware via <c>ctx.Options</c>.
    /// </summary>
    public VfsCallOptions Options { get; internal set; }

    /// <summary>
    /// Directory-listing options for a List call (recursion, search pattern, kind, etc.).
    /// Set by <c>VfsPipeline</c> before the list chain runs; middleware may read or rewrite it
    /// (e.g. force IncludeHidden = false, inject a scoping SearchPattern). Null outside a List.
    /// </summary>
    public VfsListOptions? ListOptions { get; internal set; }

    // Shared bag for within-call middleware communication.
    // Prefer typed extension methods (GetUser/SetUser) over raw access.
    // Allocated lazily - most calls never touch it.
    private SmallBag ItemsBag => _items ??= new SmallBag();

    /// <summary>
    /// Shared bag for within-call middleware communication.
    /// Prefer typed extension methods (GetUser/SetUser) over raw access.
    /// Allocated lazily - most calls never touch it.
    /// </summary>
    public IDictionary<string, object> Items => ItemsBag;

    // Internal fast-path: null when no middleware has written to Items yet.
    internal IDictionary<string, object>? RawItems => _items;

    /// <summary>
    /// Re-resolves when middleware rewrites the path (<c>PathRewriteMiddleware</c>, <c>SymlinkMiddleware</c>, etc.).
    /// Also correct for same-mount rewrites (registry lookup stamps the new mount length).
    /// Atomically updates <see cref="ResolvedNode"/>, <see cref="MountPoint"/>, and <see cref="ResolvedPath"/>.
    /// </summary>
    /// <param name="newPath">The new path to resolve and route to.</param>
    public void Reroute(VfsPath newPath)
    {
        var (node, mount, resolved) = _registry.Resolve(newPath, _ambient);
        ResolvedNode = node;
        MountPoint   = mount;
        _resolved    = resolved;
        if (resolved != newPath) // alias was followed - resolved differs from the input path
        {
            ItemsBag[VfsContextKeys.AliasOrigin]  = newPath.ToString();
            ItemsBag[VfsContextKeys.AliasFollowed] = true;
        }
        else
        {
            // If a previous Reroute set alias keys, clear them - the latest resolution wins.
            _items?.Remove(VfsContextKeys.AliasOrigin);
            _items?.Remove(VfsContextKeys.AliasFollowed);
        }
    }

    // Convenience: build the VfsNodeRequest for the current context state.
    // Called by VfsPipeline terminals just before dispatching to the node.
    internal VfsNodeRequest BuildNodeRequest()
    {
        var mountLen = MountPoint.Length;
        var fullSpan = _resolved.PathSpan;
        var relStart = mountLen < fullSpan.Length && fullSpan[mountLen] == '/'
            ? mountLen + 1
            : mountLen;
        var relPath = relStart < fullSpan.Length
            ? _resolved.WithOffset(relStart)
            : default;
        return new VfsNodeRequest(relPath, MountPoint, _items);
    }
}
