namespace Dytools.VirtualFileSystem.Nodes.LocalFs;

public sealed class LocalFsOptions
{
    // Absolute local directory the mount is rooted at.
    public string RootPath { get; set; } = "";
}

public static class LocalFsMountOptionsExtensions
{
    // Configures a LocalFsNode mount to serve the given local directory.
    //   .MountSingleton<LocalFsNode>("/dev/local", o => o.UseLocalFileSystemPath(@"C:\data"))
    public static VfsMountOptions UseLocalFileSystemPath(this VfsMountOptions options, string rootPath)
        => options.Set(new LocalFsOptions { RootPath = rootPath });
}
