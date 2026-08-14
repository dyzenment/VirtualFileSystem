using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem;

// -- Mount registry (singleton) ------------------------------------------------
// Inject this directly for global mount/unmount control.
// IVirtualFileSystem.Mount/Unmount are instance-scoped wrappers around this.
public interface IVfsMountRegistry
{
    void      Mount(string mountPoint, IVfsNode node);
    void      Unmount(string mountPoint);
    void      Alias(string alias, string target, bool isInternal = false);   // VFS-level path alias (pre-pipeline)
    void      RemoveAlias(string alias);

    // Returns the node, the matched mount-point key, and the fully-resolved VfsPath (post-alias).
    // serviceProvider is the caller's ambient scope, used to build scoped/transient-mounted
    // nodes; pass null (or omit) for instance and singleton mounts.
    // internalAllowed lets the resolution reach internal mounts - the pipeline (consumer edge)
    // passes false; a reroute passes true. A public alias to an internal mount also permits it.
    (IVfsNode Node, VfsPath MountPoint, VfsPath ResolvedPath) Resolve(
        VfsPath path, IServiceProvider? serviceProvider = null, bool internalAllowed = false);
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
    // A pre-built node - one instance for the app (singleton). isInternal hides the mount
    // from direct consumer access (reachable only via an alias or a reroute).
    IVfsBuilder Mount(string path, IVfsNode node, bool isInternal = false);

    // A factory. Reference other mounts with sp.NodeAt("/other"). The lifetime decides how
    // often the factory runs and against which scope - Singleton (once, from the root),
    // Scoped (once per DI scope), or Transient (per operation). isInternal hides the mount
    // from direct consumer access.
    IVfsBuilder Mount(string path, Func<IServiceProvider, IVfsNode> factory,
                      MountLifetime lifetime = MountLifetime.Transient, bool isInternal = false);

    // Marks a path prefix internal - every mount at or under it is hidden from direct consumer
    // access. Idiomatic: put physical mounts under "/dev" and SetInternal("/dev").
    IVfsBuilder SetInternal(string pathPrefix);

    IVfsBuilder Use<TMiddleware>() where TMiddleware : class, IVfsMiddleware;
    IVfsBuilder Use(IVfsMiddleware middleware);
    IVfsBuilder AddRewriter(Func<VfsPath, VfsPath> rewrite);  // shorthand for PathRewriteMiddleware
    IVfsBuilder Alias(string alias, string target, bool isInternal = false);   // startup path alias
    IVfsBuilder UseAliasStore<TStore>() where TStore : class, IVfsAliasStore;

    // Registers SymlinkMiddleware. Only nodes implementing ISymlinkCapableNode are checked.
    IVfsBuilder UseSymlinks();

    // Also enables symlink checking for specific node types that cannot implement
    // ISymlinkCapableNode (e.g. third-party nodes you cannot modify).
    IVfsBuilder UseSymlinks(params Type[] extraNodeTypes);
}
