using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.LocalFs;

// Mounts a local directory as a VFS path.
// Native CopyAsync and MoveAsync use File.Copy / File.Move - no data read into memory.
// ADS (StreamName in VfsNodeRequest) is supported on Windows/NTFS natively via FileStream.
// On Linux/macOS, ADS paths are passed through to the OS, which will reject them.
//
// OS-level symlinks (NTFS reparse points, Unix symlinks) are surfaced in
// VfsNodeInfo.Properties[VfsPropertyKeys.PhysicalSymlink] - not in VfsNodeInfo itself,
// since IsAliased and IsSymlink are set by the VFS core, not the node.
//
// Usage:
//   .Mount("/local/c",  sp => new LocalFsNode(@"C:\"))
//   .Mount("/tmp",      sp => new LocalFsNode(Path.GetTempPath()))
public sealed class LocalFsNode(string rootPath) : VfsNodeBase
{
    private readonly string _root = Path.GetFullPath(rootPath);

    // Activated by MountSingleton/Scoped/Transient<LocalFsNode> from the configured options.
    public LocalFsNode(VfsMountOptions options) : this(options.Require<LocalFsOptions>().RootPath) { }

    public override Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var physical = Resolve(request);
        if (!File.Exists(physical)) return Task.FromResult<Stream?>(null);
        Stream stream = new FileStream(physical, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public override Task<Stream> OpenWriteAsync(VfsNodeRequest request, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
    {
        var physical = Resolve(request);
        EnsureDirectory(physical);
        var fileMode = mode switch
        {
            VfsWriteMode.Append    => FileMode.Append,
            VfsWriteMode.CreateNew => FileMode.CreateNew,
            _                      => FileMode.Create,
        };
        Stream stream = new FileStream(physical, fileMode, FileAccess.Write,
            FileShare.None, bufferSize: 4096, useAsync: true);
        return Task.FromResult(stream);
    }

    public override Task DeleteAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var physical = Resolve(request);
        if (File.Exists(physical))           File.Delete(physical);
        else if (Directory.Exists(physical)) Directory.Delete(physical, recursive: true);
        return Task.CompletedTask;
    }

    public override Task CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        var pSrc = Resolve(src);
        var pDst = Resolve(dst);
        EnsureDirectory(pDst);
        File.Copy(pSrc, pDst, overwrite: true);
        return Task.CompletedTask;
    }

    public override Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        var pSrc = Resolve(src);
        var pDst = Resolve(dst);
        EnsureDirectory(pDst);
        if      (File.Exists(pSrc))      File.Move(pSrc, pDst, overwrite: true);
        else if (Directory.Exists(pSrc)) Directory.Move(pSrc, pDst);
        return Task.CompletedTask;
    }

    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var physical = Resolve(request);
        if (!Directory.Exists(physical)) yield break;

        foreach (var entry in Directory.EnumerateFileSystemEntries(physical))
        {
            ct.ThrowIfCancellationRequested();
            var info = BuildInfo(entry, request.Path);
            if (info is not null) yield return info;
        }
    }

    public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default)
        => Task.FromResult(GetInfoInternal(request));

    public override Task<bool> ExistsAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var physical = Resolve(request);
        return Task.FromResult(File.Exists(physical) || Directory.Exists(physical));
    }

    // -- Helpers ---------------------------------------------------------------

    // Resolve a VfsNodeRequest to a physical path.
    // ADS: if StreamName is present, appends ":streamName" to the path.
    // FileStream on Windows supports ADS natively. On Linux this will fail at the OS level.
    private string Resolve(VfsNodeRequest request)
    {
        var relSpan  = request.Path.PathSpan;
        Span<char> buf = stackalloc char[relSpan.Length + 1];
        for (int i = 0; i < relSpan.Length; i++)
            buf[i] = relSpan[i] == '/' ? Path.DirectorySeparatorChar : relSpan[i];
        var rel      = new string(buf[..relSpan.Length]);
        var combined = Path.GetFullPath(Path.Combine(_root, rel));

        if (!combined.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Path traversal denied: '{request.Path}' escapes mount root '{_root}'.");

        var streamSpan = request.Path.StreamSpan;
        return streamSpan.IsEmpty
            ? combined
            : $"{combined}:{new string(streamSpan)}";
    }

    private VfsNodeInfo? GetInfoInternal(VfsNodeRequest request)
    {
        var physical = Resolve(request);

        if (File.Exists(physical))
        {
            var fi    = new FileInfo(physical);
            var props = ImmutableDictionary<string, object>.Empty;
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                props = props.Add(VfsPropertyKeys.PhysicalSymlink, true);

            return new VfsNodeInfo
            {
                RelativePath = BuildRelativePath(fi.FullName),
                IsFile       = true,
                IsDirectory  = false,
                IsHidden     = (fi.Attributes & FileAttributes.Hidden) != 0,
                SizeBytes    = fi.Length,
                CreatedAt    = fi.CreationTimeUtc,
                ModifiedAt   = fi.LastWriteTimeUtc,
                AccessedAt   = fi.LastAccessTimeUtc,
                Properties   = props,
            };
        }

        if (Directory.Exists(physical))
        {
            var di = new DirectoryInfo(physical);
            return new VfsNodeInfo
            {
                RelativePath = BuildRelativePath(di.FullName),
                IsFile       = false,
                IsDirectory  = true,
                IsHidden     = (di.Attributes & FileAttributes.Hidden) != 0,
                CreatedAt    = di.CreationTimeUtc,
                ModifiedAt   = di.LastWriteTimeUtc,
                AccessedAt   = di.LastAccessTimeUtc,
            };
        }

        return null;
    }

    private VfsNodeInfo? BuildInfo(string physicalPath, VfsPath parentRelative)
    {
        var name     = Path.GetFileName(physicalPath);
        var relative = parentRelative.PathSpan.IsEmpty
            ? VfsPath.From(name)
            : VfsPath.From(parentRelative, name);

        var syntheticRequest = new VfsNodeRequest(relative);
        return GetInfoInternal(syntheticRequest);
    }

    // Convert a physical absolute path back to a node-relative VfsPath
    // (with casing as it actually exists on disk).
    private VfsPath BuildRelativePath(string physicalAbsolute)
    {
        var rel = physicalAbsolute[_root.Length..]
            .TrimStart(Path.DirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/');
        return VfsPath.From(rel);
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}
