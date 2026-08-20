using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Mount registry (singleton). Inject this directly for global mount/unmount control.
/// <see cref="IVirtualFileSystem"/>.Mount/Unmount are instance-scoped wrappers around this.
/// </summary>
public interface IVfsMountRegistry
{
    /// <summary>Mounts a node at the given mount point.</summary>
    void      Mount(string mountPoint, IVfsNode node);

    /// <summary>Removes the mount at the given mount point.</summary>
    void      Unmount(string mountPoint);

    /// <summary>Registers a VFS-level path alias (pre-pipeline).</summary>
    void      Alias(string alias, string target, bool isInternal = false);

    /// <summary>Removes a previously registered alias.</summary>
    void      RemoveAlias(string alias);

    /// <summary>
    /// Returns the node, the matched mount-point key, and the fully-resolved <see cref="VfsPath"/> (post-alias).
    /// </summary>
    /// <param name="path">The path to resolve.</param>
    /// <param name="serviceProvider">
    /// The caller's ambient scope, used to build scoped/transient-mounted nodes; pass null (or omit)
    /// for instance and singleton mounts.
    /// </param>
    /// <param name="internalAllowed">
    /// Lets the resolution reach internal mounts - the pipeline (consumer edge) passes false; a reroute
    /// passes true. A public alias to an internal mount also permits it.
    /// </param>
    (IVfsNode Node, VfsPath MountPoint, VfsPath ResolvedPath) Resolve(
        VfsPath path, IServiceProvider? serviceProvider = null, bool internalAllowed = false);
}

/// <summary>
/// Pluggable persistence for VFS-level path aliases.
/// Aliases are pure path rewrites applied before mount dispatch - no file exists at the alias path.
/// Default: <c>InMemoryAliasStore</c> (lost on restart). Provide a JSON-file or DB-backed store for
/// persistence across restarts.
/// </summary>
public interface IVfsAliasStore
{
    /// <summary>Persists an alias mapping.</summary>
    Task                                             SaveAsync(string alias, string target, CancellationToken ct = default);

    /// <summary>Removes a persisted alias.</summary>
    Task                                             RemoveAsync(string alias, CancellationToken ct = default);

    /// <summary>Loads all persisted alias mappings.</summary>
    IAsyncEnumerable<(string Alias, string Target)> LoadAllAsync(CancellationToken ct = default);
}

/// <summary>Fluent builder for configuring mounts, middleware, aliases, and symlink support.</summary>
public interface IVfsBuilder
{
    /// <summary>
    /// Mounts a pre-built node - one instance for the app (singleton). <paramref name="isInternal"/>
    /// hides the mount from direct consumer access (reachable only via an alias or a reroute).
    /// </summary>
    IVfsBuilder Mount(string path, IVfsNode node, bool isInternal = false);

    /// <summary>
    /// Mounts a factory. Reference other mounts with <c>sp.NodeAt("/other")</c>. The
    /// <paramref name="lifetime"/> decides how often the factory runs and against which scope -
    /// Singleton (once, from the root), Scoped (once per DI scope), or Transient (per operation).
    /// <paramref name="isInternal"/> hides the mount from direct consumer access.
    /// </summary>
    IVfsBuilder Mount(string path, Func<IServiceProvider, IVfsNode> factory,
                      MountLifetime lifetime = MountLifetime.Transient, bool isInternal = false);

    /// <summary>
    /// Marks a path prefix internal - every mount at or under it is hidden from direct consumer
    /// access. Idiomatic: put physical mounts under <c>/dev</c> and <c>SetInternal("/dev")</c>.
    /// </summary>
    IVfsBuilder SetInternal(string pathPrefix);

    /// <summary>Registers a middleware type, resolved from DI.</summary>
    IVfsBuilder Use<TMiddleware>() where TMiddleware : class, IVfsMiddleware;

    /// <summary>Registers a middleware instance.</summary>
    IVfsBuilder Use(IVfsMiddleware middleware);

    /// <summary>Shorthand for registering a <c>PathRewriteMiddleware</c>.</summary>
    IVfsBuilder AddRewriter(Func<VfsPath, VfsPath> rewrite);

    /// <summary>Registers a startup path alias.</summary>
    IVfsBuilder Alias(string alias, string target, bool isInternal = false);

    /// <summary>Registers the alias store used to persist aliases.</summary>
    IVfsBuilder UseAliasStore<TStore>() where TStore : class, IVfsAliasStore;

    /// <summary>
    /// Registers <c>SymlinkMiddleware</c>. Only nodes implementing <c>ISymlinkCapableNode</c> are checked.
    /// </summary>
    IVfsBuilder UseSymlinks();

    /// <summary>
    /// Also enables symlink checking for specific node types that cannot implement
    /// <c>ISymlinkCapableNode</c> (e.g. third-party nodes you cannot modify).
    /// </summary>
    IVfsBuilder UseSymlinks(params Type[] extraNodeTypes);
}
