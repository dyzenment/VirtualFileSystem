using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Typed, DI-native mounts. The node type is activated per mount with its configured options
/// (like AddSingleton/AddScoped/AddTransient, plus a per-mount options builder). Lifetime is in
/// the method name; the container holds the instance accordingly.
/// <code>
///   .MountSingleton&lt;LocalFsNode&gt;("/dev/local", o =&gt; o.UseLocalFileSystemPath(@"C:\"))
/// </code>
/// </summary>
public static class TypedMountExtensions
{
    /// <summary>
    /// Mounts <typeparamref name="TNode"/> at <paramref name="path"/> with a singleton lifetime
    /// (one instance shared across the container).
    /// </summary>
    /// <typeparam name="TNode">The node type to activate for this mount.</typeparam>
    /// <param name="builder">The VFS builder to add the mount to.</param>
    /// <param name="path">The mount path.</param>
    /// <param name="configure">Optional per-mount options builder.</param>
    /// <param name="isInternal">When true, marks the mount as internal (hidden from ordinary enumeration).</param>
    public static IVfsBuilder MountSingleton<TNode>(
        this IVfsBuilder builder, string path, Action<VfsMountOptions>? configure = null, bool isInternal = false)
        where TNode : class, IVfsNode
        => MountTyped<TNode>(builder, path, configure, MountLifetime.Singleton, isInternal);

    /// <summary>
    /// Mounts <typeparamref name="TNode"/> at <paramref name="path"/> with a scoped lifetime
    /// (one instance per resolution scope).
    /// </summary>
    /// <typeparam name="TNode">The node type to activate for this mount.</typeparam>
    /// <param name="builder">The VFS builder to add the mount to.</param>
    /// <param name="path">The mount path.</param>
    /// <param name="configure">Optional per-mount options builder.</param>
    /// <param name="isInternal">When true, marks the mount as internal (hidden from ordinary enumeration).</param>
    public static IVfsBuilder MountScoped<TNode>(
        this IVfsBuilder builder, string path, Action<VfsMountOptions>? configure = null, bool isInternal = false)
        where TNode : class, IVfsNode
        => MountTyped<TNode>(builder, path, configure, MountLifetime.Scoped, isInternal);

    /// <summary>
    /// Mounts <typeparamref name="TNode"/> at <paramref name="path"/> with a transient lifetime
    /// (a fresh instance per operation).
    /// </summary>
    /// <typeparam name="TNode">The node type to activate for this mount.</typeparam>
    /// <param name="builder">The VFS builder to add the mount to.</param>
    /// <param name="path">The mount path.</param>
    /// <param name="configure">Optional per-mount options builder.</param>
    /// <param name="isInternal">When true, marks the mount as internal (hidden from ordinary enumeration).</param>
    public static IVfsBuilder MountTransient<TNode>(
        this IVfsBuilder builder, string path, Action<VfsMountOptions>? configure = null, bool isInternal = false)
        where TNode : class, IVfsNode
        => MountTyped<TNode>(builder, path, configure, MountLifetime.Transient, isInternal);

    private static IVfsBuilder MountTyped<TNode>(
        IVfsBuilder builder, string path, Action<VfsMountOptions>? configure, MountLifetime lifetime, bool isInternal)
        where TNode : class, IVfsNode
    {
        var options = new VfsMountOptions();
        configure?.Invoke(options);   // build the options once, at registration

        // Pass options only to nodes that accept them; ActivatorUtilities rejects an
        // extraneous argument, so a parameterless node (e.g. InMemoryKvNode) gets none.
        var takesOptions = typeof(TNode).GetConstructors()
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(VfsMountOptions)));

        return builder.Mount(path,
            sp => takesOptions
                ? (IVfsNode)ActivatorUtilities.CreateInstance(sp, typeof(TNode), options)
                : (IVfsNode)ActivatorUtilities.CreateInstance(sp, typeof(TNode)),
            lifetime, isInternal);
    }
}
