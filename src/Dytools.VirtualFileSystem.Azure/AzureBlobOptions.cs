using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Azure;

// Config carried on the mount options for an AzureBlobNode. A null Container means
// account-wide mode (the first path segment selects the container).
public sealed class AzureBlobOptions
{
    public string? Container { get; set; }
    public string? Prefix    { get; set; }
}

public static class AzureBlobMountOptionsExtensions
{
    private static AzureBlobOptions Get(VfsMountOptions o)
    {
        var s = o.Get<AzureBlobOptions>();
        if (s is null) { s = new AzureBlobOptions(); o.Set(s); }
        return s;
    }

    // Configures an Azure Blob mount. location is null (account-wide: /mount/<container>/<blob>),
    // "container", or "container/path/prefix":
    //
    //   .MountSingleton<AzureBlobNode>("/team",    o => o.UseAzureBlob("docs"))
    //   .MountSingleton<AzureBlobNode>("/reports", o => o.UseAzureBlob("docs/reports"))
    //   .MountSingleton<AzureBlobNode>("/all",     o => o.UseAzureBlob())   // account-wide
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

    // Mirror the container/account structure into an IVfsCatalog for fast local listings (seeded
    // once, then kept fresh by write-through and manual RefreshAsync). Calling this opts caching in;
    // partition isolates the mount within a shared, partition-capable catalog and serviceKey picks a
    // keyed registration (omit either to keep its default).
    //
    //   services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));
    //   .MountSingleton<AzureBlobNode>("/team", o => o.UseAzureBlob("docs").UseAzureCachingCatalog())
    public static VfsMountOptions UseAzureCachingCatalog(
        this VfsMountOptions options, string? partition = null, object? serviceKey = null)
        => options.Set(new CatalogSelection { Partition = partition, ServiceKey = serviceKey });
}
