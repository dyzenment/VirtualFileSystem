using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

/// <summary>
/// Config carried on the mount options for a <see cref="SharePointNode"/>. A mount targets one drive
/// (document library / OneDrive), optionally rooted at a sub-path, and can optionally mirror the
/// drive's structure into an <c>IVfsCatalog</c> for fast listings.
/// </summary>
public sealed class SharePointOptions
{
    /// <summary>Target the drive directly by id (UseSharePointDrive) …</summary>
    public string? DriveId  { get; set; }

    /// <summary>… or resolve it at runtime from a site address (UseSharePointSite). Graph site address, e.g. "host:/sites/{name}".</summary>
    public string? SitePath    { get; set; }

    /// <summary>Document library display name; null = the default library.</summary>
    public string? LibraryName { get; set; }

    /// <summary>Normalized within-drive prefix rooting the mount at a sub-path; null/empty = the drive root.</summary>
    public string? RootPath { get; set; }
}

/// <summary>Fluent helpers for configuring a <see cref="SharePointNode"/> mount on <c>VfsMountOptions</c>.</summary>
public static class SharePointMountOptionsExtensions
{
    private static SharePointOptions Sp(VfsMountOptions o)
    {
        var s = o.Get<SharePointOptions>();
        if (s is null) { s = new SharePointOptions(); o.Set(s); }
        return s;
    }

    /// <summary>
    /// Mounts a Graph drive by id (from /sites/{id}/drives, /me/drive, /groups/{id}/drive).
    /// Optionally root the mount at a folder within the drive:
    /// <code>
    ///   .MountSingleton&lt;SharePointNode&gt;("/team",    o =&gt; o.UseSharePointDrive("b!AbC…"))
    ///   .MountSingleton&lt;SharePointNode&gt;("/reports", o =&gt; o.UseSharePointDrive("b!AbC…", "Shared Documents/Reports"))
    /// </code>
    /// </summary>
    /// <param name="options">The mount options being configured.</param>
    /// <param name="driveId">The Graph drive id to target.</param>
    /// <param name="rootPath">Optional folder within the drive to root the mount at.</param>
    public static VfsMountOptions UseSharePointDrive(
        this VfsMountOptions options, string driveId, string? rootPath = null)
    {
        var s = Sp(options);
        s.DriveId  = driveId;
        s.RootPath = rootPath?.Trim('/');
        return options;
    }

    /// <summary>
    /// Resolves the drive id at runtime from a site address + library name, so you don't have to look
    /// it up by hand. <paramref name="sitePath"/> is the Graph site address ("host:/sites/{name}", or
    /// just "host" for the root site) - or simply the browser URL of the site
    /// ("https://contoso.sharepoint.com/sites/Marketing"), which is converted to the Graph form for you.
    /// <paramref name="libraryName"/> is the document library's display name (null = the site's default
    /// library). On first use the node resolves and caches the id, and logs a Warning with a
    /// copy-pastable <c>UseSharePointDrive("…")</c> line - switch to that once you have the id to skip
    /// the lookup (which otherwise runs once per app start).
    /// <code>
    ///   .MountSingleton&lt;SharePointNode&gt;("/team",
    ///       o =&gt; o.UseSharePointSite("contoso.sharepoint.com:/sites/Marketing", "Documents"))
    ///   .MountSingleton&lt;SharePointNode&gt;("/team",
    ///       o =&gt; o.UseSharePointSite("https://contoso.sharepoint.com/sites/Marketing", "Documents"))
    /// </code>
    /// </summary>
    /// <param name="options">The mount options being configured.</param>
    /// <param name="sitePath">The Graph site address, or a browser URL to be converted.</param>
    /// <param name="libraryName">The document library display name; null = the site's default library.</param>
    /// <param name="rootPath">Optional folder within the drive to root the mount at.</param>
    public static VfsMountOptions UseSharePointSite(
        this VfsMountOptions options, string sitePath, string? libraryName = null, string? rootPath = null)
    {
        var s = Sp(options);
        s.SitePath    = sitePath;
        s.LibraryName = libraryName;
        s.RootPath    = rootPath?.Trim('/');
        return options;
    }

    /// <summary>
    /// Mirror the drive's structure into an <c>IVfsCatalog</c> for fast listings (kept fresh by
    /// incremental delta sync). Calling this opts caching in; <paramref name="partition"/> isolates the
    /// mount within a shared, partition-capable catalog and <paramref name="serviceKey"/> picks a keyed
    /// registration (omit either to keep its default).
    /// <code>
    ///   services.AddVfsJsonCatalog(sp =&gt; sp.NodeAt("/dev/catalog"));
    ///   .MountSingleton&lt;SharePointNode&gt;("/team", o =&gt; o.UseSharePointDrive("b!AbC…").UseSharePointCachingCatalog())
    ///   .MountSingleton&lt;SharePointNode&gt;("/hr",
    ///       o =&gt; o.UseSharePointDrive("b!XyZ…").UseSharePointCachingCatalog(partition: "hr", serviceKey: "db"))
    /// </code>
    /// </summary>
    /// <param name="options">The mount options being configured.</param>
    /// <param name="partition">Isolates the mount within a shared, partition-capable catalog.</param>
    /// <param name="serviceKey">Picks a keyed catalog registration.</param>
    public static VfsMountOptions UseSharePointCachingCatalog(
        this VfsMountOptions options, string? partition = null, object? serviceKey = null)
        => options.Set(new CatalogSelection { Partition = partition, ServiceKey = serviceKey });
}
