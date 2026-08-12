using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Middleware;

namespace Dytools.VirtualFileSystem.Internal;

internal sealed class VfsOptions
{
    // Mount points registered as keyed IVfsNode services (keyed by the path). The
    // registry builds its prefix table from these; DI owns each node's lifetime.
    public List<string>                                 MountPaths          { get; } = [];
    public List<Func<IServiceProvider, IVfsMiddleware>> MiddlewareFactories { get; } = [];
    public List<(string Alias, string Target)>          Aliases             { get; } = [];
}

internal sealed class VfsBuilder(IServiceCollection services, VfsOptions options) : IVfsBuilder
{
    // A pre-built node - one instance for the app (singleton).
    public IVfsBuilder Mount(string path, IVfsNode node)
    {
        services.AddKeyedSingleton<IVfsNode>(path, node);
        options.MountPaths.Add(path);
        return this;
    }

    // A factory - compose by nesting (e.g. new DedupeNode(new LocalFsNode(...), ...)),
    // and reference other mounts with sp.NodeAt("/other"). Lifetime picks how often the
    // factory runs and against which scope. Default Transient (rebuilt per operation);
    // pass Singleton for a stateless/shared node, Scoped to share a request's services.
    public IVfsBuilder Mount(string path, Func<IServiceProvider, IVfsNode> factory,
                             MountLifetime lifetime = MountLifetime.Transient)
    {
        Func<IServiceProvider, object?, IVfsNode> keyed = (sp, _) => factory(sp);
        switch (lifetime)
        {
            case MountLifetime.Singleton: services.AddKeyedSingleton<IVfsNode>(path, keyed); break;
            case MountLifetime.Scoped:    services.AddKeyedScoped<IVfsNode>(path, keyed);    break;
            default:                      services.AddKeyedTransient<IVfsNode>(path, keyed);  break;
        }
        options.MountPaths.Add(path);
        return this;
    }

    public IVfsBuilder Use<TMiddleware>() where TMiddleware : class, IVfsMiddleware
    {
        services.AddTransient<TMiddleware>();
        options.MiddlewareFactories.Add(sp => sp.GetRequiredService<TMiddleware>());
        return this;
    }

    public IVfsBuilder Use(IVfsMiddleware middleware)
    {
        options.MiddlewareFactories.Add(_ => middleware);
        return this;
    }

    public IVfsBuilder AddRewriter(Func<VfsPath, VfsPath> rewrite)
        => Use(new PathRewriteMiddleware(rewrite));

    public IVfsBuilder Alias(string alias, string target)
    {
        options.Aliases.Add((alias, target));
        return this;
    }

    public IVfsBuilder UseAliasStore<TStore>() where TStore : class, IVfsAliasStore
    {
        services.AddSingleton<IVfsAliasStore, TStore>();
        return this;
    }

    public IVfsBuilder UseSymlinks()
        => Use(new SymlinkMiddleware());

    public IVfsBuilder UseSymlinks(params Type[] extraNodeTypes)
        => Use(new SymlinkMiddleware(extraNodeTypes));
}
