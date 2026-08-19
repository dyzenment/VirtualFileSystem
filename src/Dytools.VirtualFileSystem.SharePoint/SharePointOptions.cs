using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

// Config carried on the mount options for a SharePointNode. A mount targets one drive
// (document library / OneDrive), optionally rooted at a sub-path, and can optionally mirror the
// drive's structure into an IVfsCatalog for fast listings.
public sealed class SharePointOptions
{
    // Target the drive directly by id (UseSharePointDrive) …
    public string? DriveId  { get; set; }

    // … or resolve it at runtime from a site address + library name (UseSharePointSite).
    public string? SitePath    { get; set; }   // Graph site address, e.g. "host:/sites/{name}"
    public string? LibraryName { get; set; }   // document library display name; null = the default library

    public string? RootPath { get; set; }
}

public static class SharePointMountOptionsExtensions
{
    private static SharePointOptions Sp(VfsMountOptions o)
    {
        var s = o.Get<SharePointOptions>();
        if (s is null) { s = new SharePointOptions(); o.Set(s); }
        return s;
    }

    // Mounts a Graph drive by id (from /sites/{id}/drives, /me/drive, /groups/{id}/drive).
    // Optionally root the mount at a folder within the drive:
    //
    //   .MountSingleton<SharePointNode>("/team",    o => o.UseSharePointDrive("b!AbC…"))
    //   .MountSingleton<SharePointNode>("/reports", o => o.UseSharePointDrive("b!AbC…", "Shared Documents/Reports"))
    public static VfsMountOptions UseSharePointDrive(
        this VfsMountOptions options, string driveId, string? rootPath = null)
    {
        var s = Sp(options);
        s.DriveId  = driveId;
        s.RootPath = rootPath?.Trim('/');
        return options;
    }

    // Resolves the drive id at runtime from a site address + library name, so you don't have to look
    // it up by hand. sitePath is the Graph site address ("host:/sites/{name}", or just "host" for the
    // root site) - or simply the browser URL of the site ("https://contoso.sharepoint.com/sites/Marketing"),
    // which is converted to the Graph form for you. libraryName is the document library's display name
    // (null = the site's default library). On first use the node resolves and caches the id, and logs a
    // Warning with a copy-pastable UseSharePointDrive("…") line - switch to that once you have the id to
    // skip the lookup (which otherwise runs once per app start).
    //
    //   .MountSingleton<SharePointNode>("/team",
    //       o => o.UseSharePointSite("contoso.sharepoint.com:/sites/Marketing", "Documents"))
    //   .MountSingleton<SharePointNode>("/team",
    //       o => o.UseSharePointSite("https://contoso.sharepoint.com/sites/Marketing", "Documents"))
    public static VfsMountOptions UseSharePointSite(
        this VfsMountOptions options, string sitePath, string? libraryName = null, string? rootPath = null)
    {
        var s = Sp(options);
        s.SitePath    = sitePath;
        s.LibraryName = libraryName;
        s.RootPath    = rootPath?.Trim('/');
        return options;
    }

    // Mirror the drive's structure into an IVfsCatalog for fast listings (kept fresh by incremental
    // delta sync). Calling this opts caching in; partition isolates the mount within a shared,
    // partition-capable catalog and serviceKey picks a keyed registration (omit either to keep its
    // default).
    //
    //   services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));
    //   .MountSingleton<SharePointNode>("/team", o => o.UseSharePointDrive("b!AbC…").UseSharePointCachingCatalog())
    //   .MountSingleton<SharePointNode>("/hr",
    //       o => o.UseSharePointDrive("b!XyZ…").UseSharePointCachingCatalog(partition: "hr", serviceKey: "db"))
    public static VfsMountOptions UseSharePointCachingCatalog(
        this VfsMountOptions options, string? partition = null, object? serviceKey = null)
        => options.Set(new CatalogSelection { Partition = partition, ServiceKey = serviceKey });
}
