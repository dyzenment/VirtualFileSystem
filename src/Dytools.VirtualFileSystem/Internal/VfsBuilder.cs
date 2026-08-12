using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Middleware;

namespace Dytools.VirtualFileSystem.Internal;

internal sealed class VfsOptions
{
    public List<(string Path, Func<IServiceProvider, IVfsNode> Factory)> Mounts             { get; } = [];
    public List<Func<IServiceProvider, IVfsMiddleware>>                  MiddlewareFactories { get; } = [];
    public List<(string Alias, string Target)>                           Aliases             { get; } = [];
}

internal sealed class VfsBuilder(IServiceCollection services, VfsOptions options) : IVfsBuilder
{
    public IVfsBuilder Mount(string path, Func<IServiceProvider, IVfsNode> factory)
    {
        options.Mounts.Add((path, factory));
        return this;
    }

    public IVfsBuilder Mount(string path, IVfsNode node) => Mount(path, _ => node);

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
