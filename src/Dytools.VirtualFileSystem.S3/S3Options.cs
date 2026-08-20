using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.S3;

/// <summary>Config carried on the mount options for an <see cref="S3Node"/>.</summary>
public sealed class S3Options
{
    /// <summary>The bucket to mount.</summary>
    public string  Bucket { get; set; } = "";

    /// <summary>Optional key prefix the mount is rooted at.</summary>
    public string? Prefix { get; set; }
}

/// <summary>Extension methods for configuring an <see cref="S3Node"/> mount on <see cref="VfsMountOptions"/>.</summary>
public static class S3MountOptionsExtensions
{
    private static S3Options Get(VfsMountOptions o)
    {
        var s = o.Get<S3Options>();
        if (s is null) { s = new S3Options(); o.Set(s); }
        return s;
    }

    /// <summary>
    /// Configures an S3 mount from <c>"bucket"</c> or <c>"bucket/key/prefix"</c> - the first segment is the
    /// bucket, the rest an optional key prefix the mount is rooted at.
    /// </summary>
    /// <example>
    /// <code>
    /// .MountSingleton&lt;S3Node&gt;("/archive", o => o.UseS3Bucket("my-bucket"))
    /// .MountSingleton&lt;S3Node&gt;("/reports", o => o.UseS3Bucket("my-bucket/reports/2026"))
    /// </code>
    /// </example>
    public static VfsMountOptions UseS3Bucket(this VfsMountOptions options, string location)
    {
        var s = location.Trim('/');
        var i = s.IndexOf('/');
        var (bucket, prefix) = i < 0 ? (s, (string?)null) : (s[..i], s[(i + 1)..]);
        var o = Get(options);
        o.Bucket = bucket;
        o.Prefix = prefix;
        return options;
    }

    /// <summary>
    /// Mirror the bucket's structure into an <c>IVfsCatalog</c> for fast local listings (seeded once, then
    /// kept fresh by write-through and manual <c>RefreshAsync</c>). Calling this opts caching in.
    /// </summary>
    /// <param name="options">The mount options being configured.</param>
    /// <param name="partition">Isolates the mount within a shared, partition-capable catalog (omit to keep its default).</param>
    /// <param name="serviceKey">Picks a keyed catalog registration (omit to keep its default).</param>
    /// <example>
    /// <code>
    /// services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));
    /// .MountSingleton&lt;S3Node&gt;("/archive", o => o.UseS3Bucket("my-bucket").UseS3CachingCatalog())
    /// </code>
    /// </example>
    public static VfsMountOptions UseS3CachingCatalog(
        this VfsMountOptions options, string? partition = null, object? serviceKey = null)
        => options.Set(new CatalogSelection { Partition = partition, ServiceKey = serviceKey });
}
