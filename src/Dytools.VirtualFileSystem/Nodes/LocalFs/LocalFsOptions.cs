namespace Dytools.VirtualFileSystem.Nodes.LocalFs;

/// <summary>Options for a LocalFsNode mount.</summary>
public sealed class LocalFsOptions
{
    /// <summary>Absolute local directory the mount is rooted at.</summary>
    public string RootPath { get; set; } = "";

    /// <summary>
    /// Whether names under this root compare case-sensitively, or null to take the platform default -
    /// insensitive on Windows and macOS, sensitive elsewhere.
    /// <para>
    /// The default is only a guess: APFS can be formatted case-sensitive and Linux can mount a
    /// case-insensitive filesystem, and only the filesystem itself knows. Set it explicitly when the
    /// mount root is one of those. It governs search-pattern matching and how the root is compared
    /// when mapping host paths to VFS paths.
    /// </para>
    /// </summary>
    public bool? CaseSensitive { get; set; }
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
