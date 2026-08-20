using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem.Catalog;

/// <summary>Dependency-injection registration helpers for the built-in JSON-backed <see cref="IVfsCatalog"/>.</summary>
public static class CatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in durable JSON catalog as <see cref="IVfsCatalog"/>. <paramref name="store"/> supplies the node
    /// it persists into (e.g. <c>sp =&gt; sp.NodeAt("/disk/catalog")</c>). Pass a <paramref name="serviceKey"/> to register
    /// several catalogs side by side and select them per mount via <c>catalogServiceKey</c>.
    /// <code>
    ///   services.AddVfsJsonCatalog(sp =&gt; sp.NodeAt("/disk/catalog"));
    ///   services.AddVfsJsonCatalog(sp =&gt; sp.NodeAt("/disk/arch"), serviceKey: "archive");
    /// </code>
    /// </summary>
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
