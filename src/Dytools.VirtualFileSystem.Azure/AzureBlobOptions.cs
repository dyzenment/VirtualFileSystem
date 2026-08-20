using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Azure;

/// <summary>
/// Config carried on the mount options for an <see cref="AzureBlobNode"/>. A null <see cref="Container"/> means
/// account-wide mode (the first path segment selects the container).
/// </summary>
public sealed class AzureBlobOptions
{
    /// <summary>The container to mount; <c>null</c> selects account-wide mode.</summary>
    public string? Container { get; set; }

    /// <summary>Optional path prefix the mount is rooted at (fixed-container mode).</summary>
    public string? Prefix    { get; set; }
}

/// <summary>Extension methods for configuring an <see cref="AzureBlobNode"/> mount on <see cref="VfsMountOptions"/>.</summary>
public static class AzureBlobMountOptionsExtensions
{
    private static AzureBlobOptions Get(VfsMountOptions o)
    {
        var s = o.Get<AzureBlobOptions>();
        if (s is null) { s = new AzureBlobOptions(); o.Set(s); }
        return s;
    }

    /// <summary>
    /// Configures an Azure Blob mount. <paramref name="location"/> is null (account-wide: <c>/mount/&lt;container&gt;/&lt;blob&gt;</c>),
    /// <c>"container"</c>, or <c>"container/path/prefix"</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// .MountSingleton&lt;AzureBlobNode&gt;("/team",    o => o.UseAzureBlob("docs"))
    /// .MountSingleton&lt;AzureBlobNode&gt;("/reports", o => o.UseAzureBlob("docs/reports"))
    /// .MountSingleton&lt;AzureBlobNode&gt;("/all",     o => o.UseAzureBlob())   // account-wide
    /// </code>
    /// </example>
    public static VfsMountOptions UseAzureBlob(this VfsMountOptions options, string? location = null)
    {
        var o = Get(options);
        if (string.IsNullOrWhiteSpace(location)) { o.Container = null; o.Prefix = null; return options; }

        var s = location.Trim('/');
        var i = s.IndexOf('/');
        var (container, prefix) = i < 0 ? (s, (string?)null) : (s[..i], s[(i + 1)..]);
        o.Container = container;
        o.Prefix    = prefix;
        return options;
    }

    /// <summary>
    /// Mirror the container/account structure into an <c>IVfsCatalog</c> for fast local listings (seeded
    /// once, then kept fresh by write-through and manual <c>RefreshAsync</c>). Calling this opts caching in.
    /// </summary>
    /// <param name="options">The mount options being configured.</param>
    /// <param name="partition">Isolates the mount within a shared, partition-capable catalog (omit to keep its default).</param>
    /// <param name="serviceKey">Picks a keyed catalog registration (omit to keep its default).</param>
    /// <example>
    /// <code>
    /// services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));
    /// .MountSingleton&lt;AzureBlobNode&gt;("/team", o => o.UseAzureBlob("docs").UseAzureCachingCatalog())
    /// </code>
    /// </example>
    public static VfsMountOptions UseAzureCachingCatalog(
        this VfsMountOptions options, string? partition = null, object? serviceKey = null)
        => options.Set(new CatalogSelection { Partition = partition, ServiceKey = serviceKey });
}
