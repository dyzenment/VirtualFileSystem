using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem;

// Typed, DI-native mounts. The node type is activated per mount with its configured options
// (like AddSingleton/AddScoped/AddTransient, plus a per-mount options builder). Lifetime is in
// the method name; the container holds the instance accordingly.
//
//   .MountSingleton<LocalFsNode>("/dev/local", o => o.UseLocalFileSystemPath(@"C:\"))
public static class TypedMountExtensions
{
    public static IVfsBuilder MountSingleton<TNode>(
        this IVfsBuilder builder, string path, Action<VfsMountOptions>? configure = null, bool isInternal = false)
        where TNode : class, IVfsNode
        => MountTyped<TNode>(builder, path, configure, MountLifetime.Singleton, isInternal);

    public static IVfsBuilder MountScoped<TNode>(
        this IVfsBuilder builder, string path, Action<VfsMountOptions>? configure = null, bool isInternal = false)
        where TNode : class, IVfsNode
        => MountTyped<TNode>(builder, path, configure, MountLifetime.Scoped, isInternal);

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
