using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

/// <summary>
/// Config carried on the mount options for a <see cref="DedupeNode"/>. The catalog (required) defaults to
/// the sole registration; use <see cref="DedupeMountOptionsExtensions.UseDedupeCatalog"/> to select a keyed
/// and/or partitioned one.
/// </summary>
public sealed class DedupeMountOptions
{
    /// <summary>Inner backing path for blobs (resolved via <c>NodeAt</c>).</summary>
    public string         Source            { get; set; } = "";
    /// <summary>The content hasher to key deduplication on; defaults to SHA-256 when unset.</summary>
    public IContentHasher? Hasher           { get; set; }
    /// <summary>Number of leading id characters used as a sharding sub-directory for blob storage.</summary>
    public int?           FanOut            { get; set; }
    /// <summary>When <c>true</c>, store new blobs under a readable file name instead of the raw hash.</summary>
    public bool           ReadableBlobNames { get; set; }
}

/// <summary>Fluent configuration helpers for a <see cref="DedupeNode"/> mount.</summary>
public static class DedupeMountOptionsExtensions
{
    private static DedupeMountOptions Dm(VfsMountOptions o)
    {
        var d = o.Get<DedupeMountOptions>();
        if (d is null) { d = new DedupeMountOptions(); o.Set(d); }
        return d;
    }

    /// <summary>
    /// Sets the backing store for blobs, given as a VFS path (resolved via <c>NodeAt</c>) - configure the
    /// backend once and reference it here.
    /// </summary>
    public static VfsMountOptions UseSource(this VfsMountOptions o, string vfsPath)
    { Dm(o).Source = vfsPath; return o; }

    /// <summary>
    /// Stores new blobs under the file name that first saved the content (with a <c>-N</c> suffix on
    /// collision) instead of the hash. Dedup still keys on the content hash.
    /// </summary>
    public static VfsMountOptions UseReadableBlobNames(this VfsMountOptions o, bool enabled = true)
    { Dm(o).ReadableBlobNames = enabled; return o; }

    /// <summary>
    /// Sets the blob fan-out (sharding by leading id characters).
    /// Blobs are stored at the root of the <see cref="UseSource"/> path. To nest them under a subfolder,
    /// point <see cref="UseSource"/> at that subfolder - e.g. <c>UseSource("/dev/store/blobs")</c> - rather
    /// than a separate option.
    /// </summary>
    public static VfsMountOptions UseFanOut(this VfsMountOptions o, int fanOut)
    { Dm(o).FanOut = fanOut; return o; }

    /// <summary>Sets the content hasher used to key deduplication.</summary>
    public static VfsMountOptions UseContentHasher(this VfsMountOptions o, IContentHasher hasher)
    { Dm(o).Hasher = hasher; return o; }

    /// <summary>
    /// Selects which <c>IVfsCatalog</c> backs this dedupe mount when the default registration isn't the one
    /// you want: <paramref name="partition"/> isolates the mount within a shared, partition-capable catalog;
    /// <paramref name="serviceKey"/> picks a keyed registration. Omit either to keep its default. The catalog
    /// itself is always required - this only changes which one is resolved.
    /// </summary>
    /// <remarks>
    /// <code>
    ///   .MountSingleton&lt;DedupeNode&gt;("/files", o => o.UseSource("/dev/store")
    ///       .UseDedupeCatalog(partition: "files", serviceKey: "db"))
    /// </code>
    /// </remarks>
    public static VfsMountOptions UseDedupeCatalog(
        this VfsMountOptions o, string? partition = null, object? serviceKey = null)
        => o.Set(new CatalogSelection { Partition = partition, ServiceKey = serviceKey });
}
