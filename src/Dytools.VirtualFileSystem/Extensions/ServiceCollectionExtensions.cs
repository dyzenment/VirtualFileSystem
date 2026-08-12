using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Internal;

namespace Dytools.VirtualFileSystem.Extensions;

public static class ServiceCollectionExtensions
{
    // Program.cs:
    //   builder.Services
    //       .AddVirtualFileSystem()
    //       .Use<UserIdentityMiddleware>()               // first - sets user for all others
    //       .UseSymlinks()                               // enable node-level symlink following
    //       .AddRewriter(p => p.Replace(@"C:\", "/local/c/"))
    //       .Mount("/local/c",  sp => new LocalFsNode(@"C:\"))
    //       .Mount("/tmp",      sp => new LocalFsNode(Path.GetTempPath()))
    //       .Mount("/kv",       sp => new InMemoryKvNode())
    //       .Alias("/logs",     "/local/c/app/logs");
    public static IVfsBuilder AddVirtualFileSystem(this IServiceCollection services)
    {
        var opts = new VfsOptions();

        services.AddSingleton(opts);
        services.AddSingleton<IVfsAliasStore, InMemoryAliasStore>();

        // The registry populates itself from the accumulated options the first time it's
        // resolved (which happens automatically when anything first uses the VFS). Startup
        // aliases and instance/singleton mounts are applied here; scoped/transient mounts
        // are stored as resolvers and built per operation. No manual init step required.
        services.AddSingleton<IVfsMountRegistry>(sp =>
        {
            var registry = new VfsMountRegistry(sp);   // root; keyed nodes fall back to this provider
            var o = sp.GetRequiredService<VfsOptions>();

            foreach (var (alias, target) in o.Aliases)
                registry.Alias(alias, target);

            foreach (var path in o.MountPaths)          // each mount is a keyed IVfsNode service; DI owns the lifetime
                registry.MountKeyed(path);

            return registry;
        });

        // Pipeline is a singleton - built once from the accumulated options.
        services.AddSingleton(sp =>
        {
            var o  = sp.GetRequiredService<VfsOptions>();
            var mw = o.MiddlewareFactories.Select(f => f(sp)).ToList();
            return new VfsPipeline(mw);
        });

        // IVirtualFileSystem is transient - each consumer gets a fresh instance with its
        // own instance-mount list. Shares the singleton registry + pipeline. `sp` is the
        // scope this vfs is resolved from (the web request scope, in a request); it's
        // captured so scoped/transient-mounted nodes resolve against that same scope.
        services.AddTransient<IVirtualFileSystem>(sp =>
            new DefaultVirtualFileSystem(
                sp.GetRequiredService<IVfsMountRegistry>(),
                sp.GetRequiredService<VfsPipeline>(),
                ambient: sp));

        return new VfsBuilder(services, opts);
    }

    // Optional. The registry self-populates from the options on first use, so this is
    // no longer required. Call it at startup if you'd rather build the mounts (and
    // surface any mount-factory errors) eagerly instead of on the first VFS operation:
    //   app.Services.InitializeVirtualFileSystem();   // forces the registry to build now
    public static void InitializeVirtualFileSystem(this IServiceProvider services)
        => services.GetRequiredService<IVfsMountRegistry>();
}
