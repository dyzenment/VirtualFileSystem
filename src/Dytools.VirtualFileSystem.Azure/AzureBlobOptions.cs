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

    /// <summary>
    /// Whether every write computes an MD5 of the content and records it as the blob's Content-MD5.
    /// See <c>UseAzureContentMd5</c>.
    /// </summary>
    public bool ContentMd5OnUpload { get; set; }
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

    /// <summary>
    /// Computes an MD5 of every write on this mount and records it as the blob's Content-MD5.
    /// <para>
    /// Azure never computes a content hash itself - Content-MD5 is only ever what an uploader supplied,
    /// which is why a blob written by anything else has none. Turning this on makes the hash free to
    /// read back afterwards instead of costing a full download, and the bytes are already streaming
    /// past on the way up, so computing it adds no transfer.
    /// </para>
    /// <para>
    /// The cost is one extra request per write to attach the header once the content is committed, and
    /// MD5 is a checksum rather than a content identity - fine for telling two files apart, not for
    /// trusting that two are the same against an adversary.
    /// </para>
    /// </summary>
    public static VfsMountOptions UseAzureContentMd5(this VfsMountOptions options, bool enabled = true)
    {
        Get(options).ContentMd5OnUpload = enabled;
        return options;
    }
}
