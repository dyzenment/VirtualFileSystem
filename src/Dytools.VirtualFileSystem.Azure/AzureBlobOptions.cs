using Dytools.VirtualFileSystem;

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
    // Configures an Azure Blob mount. location is null (account-wide: /mount/<container>/<blob>),
    // "container", or "container/path/prefix":
    //
    //   .MountSingleton<AzureBlobNode>("/team",    o => o.UseAzureBlob("docs"))
    //   .MountSingleton<AzureBlobNode>("/reports", o => o.UseAzureBlob("docs/reports"))
    //   .MountSingleton<AzureBlobNode>("/all",     o => o.UseAzureBlob())   // account-wide
    public static VfsMountOptions UseAzureBlob(this VfsMountOptions options, string? location = null)
    {
        if (string.IsNullOrWhiteSpace(location))
            return options.Set(new AzureBlobOptions());   // account-wide

        var s = location.Trim('/');
        var i = s.IndexOf('/');
        var (container, prefix) = i < 0 ? (s, (string?)null) : (s[..i], s[(i + 1)..]);
        return options.Set(new AzureBlobOptions { Container = container, Prefix = prefix });
    }
}
