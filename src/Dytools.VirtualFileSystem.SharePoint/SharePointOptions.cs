using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

// Config carried on the mount options for a SharePointNode. A mount targets one drive
// (document library / OneDrive), optionally rooted at a sub-path, and can optionally mirror the
// drive's structure into an IVfsCatalog for fast listings.
public sealed class SharePointOptions
{
    public string  DriveId  { get; set; } = "";
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
