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
    // serviceProvider is the caller's ambient scope, used to build scoped/transient-mounted
    // nodes; pass null (or omit) for instance and singleton mounts.
    (IVfsNode Node, VfsPath MountPoint, VfsPath ResolvedPath) Resolve(
        VfsPath path, IServiceProvider? serviceProvider = null);
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
    // A pre-built node - one instance for the app (singleton).
    IVfsBuilder Mount(string path, IVfsNode node);

    // A factory. Compose by nesting (new DedupeNode(new LocalFsNode(...), ...)) and
    // reference other mounts with sp.NodeAt("/other"). The lifetime decides how often the
    // factory runs and against which scope - Singleton (once, from the root), Scoped (once
    // per DI scope; in a web request the request scope, so the node shares its services),
    // or Transient (per operation). Scoped/Transient require IVirtualFileSystem to be
    // resolved from a DI scope.
    IVfsBuilder Mount(string path, Func<IServiceProvider, IVfsNode> factory,
                      MountLifetime lifetime = MountLifetime.Transient);

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
