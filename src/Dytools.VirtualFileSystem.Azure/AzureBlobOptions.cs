using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.Azure;

// Config carried on the mount options for an AzureBlobNode. A null Container means
// account-wide mode (the first path segment selects the container).
public sealed class AzureBlobOptions
{
    public string? Container  { get; set; }
    public string? Prefix     { get; set; }
    public bool    UseCatalog { get; set; }   // opt-in via UseCachingCatalog
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

    // Mirror the container's structure into an IVfsCatalog for fast local listings. By default the
    // mirror is seeded once (a full listing), served locally, and kept up to date write-through for
    // changes made through this VFS; call ICatalogMirror.RefreshAsync to re-sync against external
    // changes. Select the catalog with UseCatalogServiceKey / UseCatalogPartition.
    //
    //   services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));
    //   .MountSingleton<AzureBlobNode>("/team", o => o.UseAzureBlob("docs").UseCachingCatalog())
    public static VfsMountOptions UseCachingCatalog(this VfsMountOptions options)
    {
        Get(options).UseCatalog = true;
        return options;
    }
}
