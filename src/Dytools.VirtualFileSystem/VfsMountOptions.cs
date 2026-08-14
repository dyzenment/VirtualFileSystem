namespace Dytools.VirtualFileSystem;

// Per-mount configuration built by the typed mount methods (MountSingleton/Scoped/Transient<T>)
// and consumed when the node is activated. Node packages contribute `Use...` extension methods
// that stash a typed options object here; the node reads it back at construction. Modeled on
// EF Core's DbContextOptionsBuilder - one builder, provider-contributed `Use...` calls.
public sealed class VfsMountOptions
{
    private readonly Dictionary<Type, object> _extensions = new();

    // Selects a keyed DI registration for a node's dependency when more than one is registered
    // (set via UseServiceKey). Null resolves the default (unkeyed) registration.
    public object? ServiceKey { get; set; }

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
    // Selects which keyed registration of a node's dependency to resolve at activation.
    public static VfsMountOptions UseServiceKey(this VfsMountOptions options, object serviceKey)
    {
        options.ServiceKey = serviceKey;
        return options;
    }
}
