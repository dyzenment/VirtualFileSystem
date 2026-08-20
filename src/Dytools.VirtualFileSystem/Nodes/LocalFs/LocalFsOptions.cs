namespace Dytools.VirtualFileSystem.Nodes.LocalFs;

/// <summary>Options for a LocalFsNode mount.</summary>
public sealed class LocalFsOptions
{
    /// <summary>Absolute local directory the mount is rooted at.</summary>
    public string RootPath { get; set; } = "";
}

/// <summary>Mount-options extensions for configuring a LocalFsNode.</summary>
public static class LocalFsMountOptionsExtensions
{
    /// <summary>
    /// Configures a LocalFsNode mount to serve the given local directory.
    /// <code>
    ///   .MountSingleton&lt;LocalFsNode&gt;("/dev/local", o =&gt; o.UseLocalFileSystemPath(@"C:\data"))
    /// </code>
    /// </summary>
    /// <param name="options">The mount options being configured.</param>
    /// <param name="rootPath">Absolute local directory to root the mount at.</param>
    public static VfsMountOptions UseLocalFileSystemPath(this VfsMountOptions options, string rootPath)
        => options.Set(new LocalFsOptions { RootPath = rootPath });
}
