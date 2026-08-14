using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.S3;

// Config carried on the mount options for an S3Node.
public sealed class S3Options
{
    public string  Bucket     { get; set; } = "";
    public string? Prefix     { get; set; }
    public bool    UseCatalog { get; set; }   // opt-in via UseCachingCatalog
}

public static class S3MountOptionsExtensions
{
    private static S3Options Get(VfsMountOptions o)
    {
        var s = o.Get<S3Options>();
        if (s is null) { s = new S3Options(); o.Set(s); }
        return s;
    }

    // Configures an S3 mount from "bucket" or "bucket/key/prefix" - the first segment is the
    // bucket, the rest an optional key prefix the mount is rooted at:
    //
    //   .MountSingleton<S3Node>("/archive", o => o.UseS3Bucket("my-bucket"))
    //   .MountSingleton<S3Node>("/reports", o => o.UseS3Bucket("my-bucket/reports/2026"))
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

    // Mirror the bucket's structure into an IVfsCatalog for fast local listings. S3 has no cheap
    // delta, so the mirror is seeded once (a full listing), served locally, and kept up to date
    // write-through for changes made through this VFS; call ICatalogMirror.RefreshAsync to re-sync
    // against external changes. Select the catalog with UseCatalogServiceKey / UseCatalogPartition.
    //
    //   services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));
    //   .MountSingleton<S3Node>("/archive", o => o.UseS3Bucket("my-bucket").UseCachingCatalog())
    public static VfsMountOptions UseCachingCatalog(this VfsMountOptions options)
    {
        Get(options).UseCatalog = true;
        return options;
    }
}
