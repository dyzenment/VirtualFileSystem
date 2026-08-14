using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Middleware;

namespace Dytools.VirtualFileSystem.Internal;

internal sealed class VfsOptions
{
    // Mount points registered as keyed IVfsNode services (keyed by the path), with whether
    // the mount is explicitly internal. The registry builds its prefix table from these.
    public List<(string Path, bool Internal)>           Mounts              { get; } = [];
    public List<string>                                 InternalPrefixes    { get; } = [];
    public List<Func<IServiceProvider, IVfsMiddleware>> MiddlewareFactories { get; } = [];
    public List<(string Alias, string Target, bool Internal)> Aliases       { get; } = [];
}

internal sealed class VfsBuilder(IServiceCollection services, VfsOptions options) : IVfsBuilder
{
    // A pre-built node - one instance for the app (singleton).
    public IVfsBuilder Mount(string path, IVfsNode node, bool isInternal = false)
    {
        services.AddKeyedSingleton<IVfsNode>(path, node);
        options.Mounts.Add((path, isInternal));
        return this;
    }

    // A factory - reference other mounts with sp.NodeAt("/other"). Lifetime picks how often
    // the factory runs and against which scope. isInternal hides the mount from direct
    // consumer access (reachable only via an alias or a reroute).
    public IVfsBuilder Mount(string path, Func<IServiceProvider, IVfsNode> factory,
                             MountLifetime lifetime = MountLifetime.Transient, bool isInternal = false)
    {
        Func<IServiceProvider, object?, IVfsNode> keyed = (sp, _) => factory(sp);
        switch (lifetime)
        {
            case MountLifetime.Singleton: services.AddKeyedSingleton<IVfsNode>(path, keyed); break;
            case MountLifetime.Scoped:    services.AddKeyedScoped<IVfsNode>(path, keyed);    break;
            default:                      services.AddKeyedTransient<IVfsNode>(path, keyed);  break;
        }
        options.Mounts.Add((path, isInternal));
        return this;
    }

    // Marks a path prefix internal - every mount at or under it is hidden from direct
    // consumer access (still reachable via an alias or a reroute). The idiomatic pattern is
    // to put physical mounts under "/dev" and SetInternal("/dev").
    public IVfsBuilder SetInternal(string pathPrefix)
    {
        options.InternalPrefixes.Add(pathPrefix);
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

    // A pure path rewrite (no processing). isInternal keeps the alias itself hidden; a
    // public (non-internal) alias to an internal mount is the sanctioned door to it.
    public IVfsBuilder Alias(string alias, string target, bool isInternal = false)
    {
        options.Aliases.Add((alias, target, isInternal));
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
