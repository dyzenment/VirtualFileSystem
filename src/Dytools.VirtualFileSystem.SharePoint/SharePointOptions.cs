using Dytools.VirtualFileSystem;

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

    // Caching catalog (opt-in via UseCachingCatalog). When set, the node keeps a mirror of the
    // namespace in an IVfsCatalog resolved from DI. Which catalog / which partition is selected
    // with UseCatalogServiceKey / UseCatalogPartition, exactly like the dedupe node.
    public bool UseCatalog { get; set; }
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
    // root site); libraryName is the document library's display name (null = the site's default
    // library). On first use the node resolves and caches the id, and logs a Warning with a
    // copy-pastable UseSharePointDrive("…") line - switch to that once you have the id to skip the
    // lookup (which otherwise runs once per app start).
    //
    //   .MountSingleton<SharePointNode>("/team",
    //       o => o.UseSharePointSite("contoso.sharepoint.com:/sites/Marketing", "Documents"))
    public static VfsMountOptions UseSharePointSite(
        this VfsMountOptions options, string sitePath, string? libraryName = null, string? rootPath = null)
    {
        var s = Sp(options);
        s.SitePath    = sitePath;
        s.LibraryName = libraryName;
        s.RootPath    = rootPath?.Trim('/');
        return options;
    }

    // Mirror the drive's structure into an IVfsCatalog (register one, e.g. AddVfsJsonCatalog, or a
    // database-backed catalog for large libraries). Directory listings then serve from the local
    // catalog after a fast incremental delta sync - the big speedup for libraries with thousands
    // of items - while reads and mutations still hit SharePoint directly and keep the catalog fresh.
    //
    // Select the catalog and partition with UseCatalogServiceKey / UseCatalogPartition:
    //
    //   services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));
    //   .MountSingleton<SharePointNode>("/team",
    //       o => o.UseSharePointDrive("b!AbC…").UseCachingCatalog())
    //   .MountSingleton<SharePointNode>("/hr",
    //       o => o.UseSharePointDrive("b!XyZ…").UseCachingCatalog().UseCatalogServiceKey("db").UseCatalogPartition("hr"))
    public static VfsMountOptions UseCachingCatalog(this VfsMountOptions options)
    {
        Sp(options).UseCatalog = true;
        return options;
    }
}
