using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem.Catalog;

public static class CatalogServiceCollectionExtensions
{
    // Registers the built-in durable JSON catalog as IVfsCatalog. `store` supplies the node
    // it persists into (e.g. sp => sp.NodeAt("/disk/catalog")). Pass a serviceKey to register
    // several catalogs side by side and select them per mount via catalogServiceKey.
    //
    //   services.AddVfsJsonCatalog(sp => sp.NodeAt("/disk/catalog"));
    //   services.AddVfsJsonCatalog(sp => sp.NodeAt("/disk/arch"), serviceKey: "archive");
    public static IServiceCollection AddVfsJsonCatalog(
        this IServiceCollection services,
        Func<IServiceProvider, IVfsNode> store,
        string catalogPath = ".vfs-catalog.json",
        object? serviceKey = null)
    {
        if (serviceKey is null)
            services.AddSingleton<IVfsCatalog>(sp => new JsonFileVfsCatalog(store(sp), catalogPath));
        else
            services.AddKeyedSingleton<IVfsCatalog>(serviceKey, (sp, _) => new JsonFileVfsCatalog(store(sp), catalogPath));
        return services;
    }
}
