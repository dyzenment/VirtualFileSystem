namespace Dytools.VirtualFileSystem;

/// <summary>
/// Per-mount configuration built by the typed mount methods (<c>MountSingleton/Scoped/Transient&lt;T&gt;</c>)
/// and consumed when the node is activated. Node packages contribute <c>Use...</c> extension methods
/// that stash a typed options object here; the node reads it back at construction. Modeled on
/// EF Core's <c>DbContextOptionsBuilder</c> - one builder, provider-contributed <c>Use...</c> calls.
/// </summary>
public sealed class VfsMountOptions
{
    private readonly Dictionary<Type, object> _extensions = new();

    /// <summary>Stash a typed options object (called by a node's <c>Use...</c> extension).</summary>
    /// <typeparam name="T">The options type to stash.</typeparam>
    /// <returns>This instance, for chaining.</returns>
    public VfsMountOptions Set<T>(T extension) where T : class
    {
        _extensions[typeof(T)] = extension;
        return this;
    }

    /// <summary>Read a stashed options object, or null if the matching <c>Use...</c> was never called.</summary>
    /// <typeparam name="T">The options type to read.</typeparam>
    public T? Get<T>() where T : class
        => _extensions.TryGetValue(typeof(T), out var e) ? (T)e : null;

    /// <summary>Read a stashed options object, throwing a clear error if it's absent.</summary>
    /// <typeparam name="T">The options type to read.</typeparam>
    /// <exception cref="InvalidOperationException">The required options object was never stashed.</exception>
    public T Require<T>() where T : class
        => Get<T>() ?? throw new InvalidOperationException(
            $"Mount options are missing required configuration '{typeof(T).Name}'. " +
            "Call the matching Use... method when mounting this node.");
}
