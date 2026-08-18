namespace Dytools.VirtualFileSystem;

// Per-mount configuration built by the typed mount methods (MountSingleton/Scoped/Transient<T>)
// and consumed when the node is activated. Node packages contribute `Use...` extension methods
// that stash a typed options object here; the node reads it back at construction. Modeled on
// EF Core's DbContextOptionsBuilder - one builder, provider-contributed `Use...` calls.
public sealed class VfsMountOptions
{
    private readonly Dictionary<Type, object> _extensions = new();

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
