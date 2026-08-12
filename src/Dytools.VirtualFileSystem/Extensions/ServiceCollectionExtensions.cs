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
        services.AddSingleton<IVfsMountRegistry, VfsMountRegistry>();
        services.AddSingleton<IVfsAliasStore,    InMemoryAliasStore>();

        // Pipeline is a singleton - built once from the accumulated options.
        services.AddSingleton(sp =>
        {
            var o  = sp.GetRequiredService<VfsOptions>();
            var mw = o.MiddlewareFactories.Select(f => f(sp)).ToList();
            return new VfsPipeline(mw);
        });

        // IVirtualFileSystem is transient - each consumer gets a fresh instance
        // with its own instance-mount list. Shares the singleton registry + pipeline.
        services.AddTransient<IVirtualFileSystem>(sp =>
            new DefaultVirtualFileSystem(
                sp.GetRequiredService<IVfsMountRegistry>(),
                sp.GetRequiredService<VfsPipeline>()));

        return new VfsBuilder(services, opts);
    }

    // For non-ASP.NET projects (console, WinForms, background services):
    //   host.Services.InitializeVirtualFileSystem();
    public static void InitializeVirtualFileSystem(this IServiceProvider services)
    {
        var registry = services.GetRequiredService<IVfsMountRegistry>();
        var opts     = services.GetRequiredService<VfsOptions>();

        foreach (var (alias, target) in opts.Aliases)
            registry.Alias(alias, target);

        foreach (var (path, factory) in opts.Mounts)
            registry.Mount(path, factory(services));
    }
}
