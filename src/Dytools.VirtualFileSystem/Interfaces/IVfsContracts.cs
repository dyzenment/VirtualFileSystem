using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem;

// -- Mount registry (singleton) ------------------------------------------------
// Inject this directly for global mount/unmount control.
// IVirtualFileSystem.Mount/Unmount are instance-scoped wrappers around this.
public interface IVfsMountRegistry
{
    void      Mount(string mountPoint, IVfsNode node);
    void      Unmount(string mountPoint);
    void      Alias(string alias, string target);      // VFS-level path alias (pre-pipeline)
    void      RemoveAlias(string alias);

    // Returns the node, the matched mount-point key, and the fully-resolved VfsPath (post-alias).
    (IVfsNode Node, VfsPath MountPoint, VfsPath ResolvedPath) Resolve(VfsPath path);
}

// -- Alias store ---------------------------------------------------------------
// Pluggable persistence for VFS-level path aliases.
// Aliases are pure path rewrites applied before mount dispatch - no file exists
// at the alias path. Default: InMemoryAliasStore (lost on restart).
// Provide a JSON-file or DB-backed store for persistence across restarts.
public interface IVfsAliasStore
{
    Task                                             SaveAsync(string alias, string target, CancellationToken ct = default);
    Task                                             RemoveAsync(string alias, CancellationToken ct = default);
    IAsyncEnumerable<(string Alias, string Target)> LoadAllAsync(CancellationToken ct = default);
}

// -- Builder -------------------------------------------------------------------
public interface IVfsBuilder
{
    IVfsBuilder Mount(string path, Func<IServiceProvider, IVfsNode> factory);
    IVfsBuilder Mount(string path, IVfsNode node);
    IVfsBuilder Use<TMiddleware>() where TMiddleware : class, IVfsMiddleware;
    IVfsBuilder Use(IVfsMiddleware middleware);
    IVfsBuilder AddRewriter(Func<VfsPath, VfsPath> rewrite);  // shorthand for PathRewriteMiddleware
    IVfsBuilder Alias(string alias, string target);          // startup path alias
    IVfsBuilder UseAliasStore<TStore>() where TStore : class, IVfsAliasStore;

    // Registers SymlinkMiddleware. Only nodes implementing ISymlinkCapableNode are checked.
    IVfsBuilder UseSymlinks();

    // Also enables symlink checking for specific node types that cannot implement
    // ISymlinkCapableNode (e.g. third-party nodes you cannot modify).
    IVfsBuilder UseSymlinks(params Type[] extraNodeTypes);
}
