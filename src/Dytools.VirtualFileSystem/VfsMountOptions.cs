namespace Dytools.VirtualFileSystem;

// Per-mount configuration built by the typed mount methods (MountSingleton/Scoped/Transient<T>)
// and consumed when the node is activated. Node packages contribute `Use...` extension methods
// that stash a typed options object here; the node reads it back at construction. Modeled on
// EF Core's DbContextOptionsBuilder - one builder, provider-contributed `Use...` calls.
public sealed class VfsMountOptions
{
    private readonly Dictionary<Type, object> _extensions = new();

    // Selects a keyed IVfsCatalog registration when a catalog-backed node (dedupe, SharePoint
    // cache, …) has more than one to choose from (set via UseCatalogServiceKey). Named explicitly
    // so it isn't confused with keying the node's own service. Null resolves the default catalog.
    public object? CatalogServiceKey { get; set; }

    // Isolates this mount within a shared, partition-capable catalog (set via UseCatalogPartition).
    // Null means the catalog's default (un-partitioned) view.
    public string? CatalogPartitionKey { get; set; }

    // Stash a typed options object (called by a node's Use... extension).
    public VfsMountOptions Set<T>(T extension) where T : class
    {
        _extensions[typeof(T)] = extension;
        return this;
    }

    // Read a stashed options object, or null if the matching Use... was never called.
    public T? Get<T>() where T : class
        => _extensions.TryGetValue(typeof(T), out var e) ? (T)e : null;

    // Read a stashed options object, throwing a clear error if it's absent.
    public T Require<T>() where T : class
        => Get<T>() ?? throw new InvalidOperationException(
            $"Mount options are missing required configuration '{typeof(T).Name}'. " +
            "Call the matching Use... method when mounting this node.");
}

public static class VfsMountOptionsExtensions
{
    // Selects which keyed IVfsCatalog registration a catalog-backed node resolves at activation.
    public static VfsMountOptions UseCatalogServiceKey(this VfsMountOptions options, object serviceKey)
    {
        options.CatalogServiceKey = serviceKey;
        return options;
    }

    // Isolates this mount within a shared, partition-capable catalog (IPartitionedVfsCatalog).
    // Used by any catalog-backed node (dedupe, SharePoint cache, …).
    public static VfsMountOptions UseCatalogPartition(this VfsMountOptions options, string partitionKey)
    {
        options.CatalogPartitionKey = partitionKey;
        return options;
    }
}
