namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

// Config carried on the mount options for a DedupeNode. The catalog is selected separately via
// UseCatalogServiceKey (which keyed IVfsCatalog) and UseCatalogPartition (isolation).
public sealed class DedupeMountOptions
{
    public string         Source            { get; set; } = "";   // inner backing path (resolved via NodeAt)
    public IContentHasher? Hasher           { get; set; }
    public string?        BlobPrefix        { get; set; }
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

    public static VfsMountOptions UseBlobPrefix(this VfsMountOptions o, string blobPrefix)
    { Dm(o).BlobPrefix = blobPrefix; return o; }

    public static VfsMountOptions UseFanOut(this VfsMountOptions o, int fanOut)
    { Dm(o).FanOut = fanOut; return o; }

    public static VfsMountOptions UseContentHasher(this VfsMountOptions o, IContentHasher hasher)
    { Dm(o).Hasher = hasher; return o; }
}
