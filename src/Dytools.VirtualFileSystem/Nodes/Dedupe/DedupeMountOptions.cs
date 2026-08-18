using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

// Config carried on the mount options for a DedupeNode. The catalog (required) defaults to the sole
// registration; use UseDedupeCatalog to select a keyed and/or partitioned one.
public sealed class DedupeMountOptions
{
    public string         Source            { get; set; } = "";   // inner backing path (resolved via NodeAt)
    public IContentHasher? Hasher           { get; set; }
    public int?           FanOut            { get; set; }
    public bool           ReadableBlobNames { get; set; }
}

public static class DedupeMountOptionsExtensions
{
    private static DedupeMountOptions Dm(VfsMountOptions o)
    {
        var d = o.Get<DedupeMountOptions>();
        if (d is null) { d = new DedupeMountOptions(); o.Set(d); }
        return d;
    }

    // The backing store for blobs, given as a VFS path (resolved via NodeAt) - configure the
    // backend once and reference it here.
    public static VfsMountOptions UseSource(this VfsMountOptions o, string vfsPath)
    { Dm(o).Source = vfsPath; return o; }

    // Store new blobs under the file name that first saved the content (with a -N suffix on
    // collision) instead of the hash. Dedup still keys on the content hash.
    public static VfsMountOptions UseReadableBlobNames(this VfsMountOptions o, bool enabled = true)
    { Dm(o).ReadableBlobNames = enabled; return o; }

    // (Blobs are stored at the root of the UseSource path. To nest them under a subfolder, point
    // UseSource at that subfolder - e.g. UseSource("/dev/store/blobs") - rather than a separate option.)
    public static VfsMountOptions UseFanOut(this VfsMountOptions o, int fanOut)
    { Dm(o).FanOut = fanOut; return o; }

    public static VfsMountOptions UseContentHasher(this VfsMountOptions o, IContentHasher hasher)
    { Dm(o).Hasher = hasher; return o; }

    // Select which IVfsCatalog backs this dedupe mount when the default registration isn't the one
    // you want: partition isolates the mount within a shared, partition-capable catalog; serviceKey
    // picks a keyed registration. Omit either to keep its default. The catalog itself is always
    // required - this only changes which one is resolved.
    //
    //   .MountSingleton<DedupeNode>("/files", o => o.UseSource("/dev/store")
    //       .UseDedupeCatalog(partition: "files", serviceKey: "db"))
    public static VfsMountOptions UseDedupeCatalog(
        this VfsMountOptions o, string? partition = null, object? serviceKey = null)
        => o.Set(new CatalogSelection { Partition = partition, ServiceKey = serviceKey });
}
