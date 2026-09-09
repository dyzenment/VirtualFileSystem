using System.Collections.Immutable;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Nodes.LocalFs.Trash;

namespace Dytools.VirtualFileSystem.Nodes.LocalFs;

/// <summary>
/// Mounts a local directory as a VFS path.
/// Native <see cref="CopyAsync"/> and <see cref="MoveAsync"/> use <see cref="File.Copy(string, string)"/> /
/// <see cref="File.Move(string, string)"/> - no data read into memory.
/// ADS (<c>StreamName</c> in <see cref="VfsNodeRequest"/>) is supported on Windows/NTFS natively via
/// <see cref="FileStream"/>. On Linux/macOS, ADS paths are passed through to the OS, which will reject them.
/// <para>
/// OS-level symlinks (NTFS reparse points, Unix symlinks) are surfaced in
/// <c>VfsNodeInfo.Properties[VfsPropertyKeys.PhysicalSymlink]</c> - not in <see cref="VfsNodeInfo"/> itself,
/// since <c>IsAliased</c> and <c>IsSymlink</c> are set by the VFS core, not the node.
/// </para>
/// </summary>
/// <remarks>
/// Usage:
/// <code>
///   .Mount("/local/c",  sp => new LocalFsNode(@"C:\"))
///   .Mount("/tmp",      sp => new LocalFsNode(Path.GetTempPath()))
///   .Mount("/host",     sp => new LocalFsNode(""))       // the whole machine
/// </code>
/// <para>
/// A root of <c>""</c> or <c>"/"</c> means the whole machine, which is what a file picker needs:
/// the user can choose anything, and a path that resolves nowhere would otherwise force a copy of a
/// file onto itself. On Linux and macOS that is just a node rooted at "/". On Windows, which has no
/// single root, the first path segment names the volume - <c>/host/c/Users/mike</c> is
/// <c>C:\Users\mike</c>, and <c>/host/unc/server/share</c> is <c>\\server\share</c>. Listing the
/// mount root then yields the volumes.
/// </para>
/// <para>
/// Whole-machine roots have no containment to enforce, so mount one where the process already has
/// the user's authority - a desktop app behind a picker - and prefer directory-rooted mounts, which
/// also rank ahead of it, for the places a service should be confined to.
/// </para>
/// </remarks>
public sealed class LocalFsNode(string rootPath, bool? caseSensitive = null) : VfsNodeBase, ILocalPathMapping
{
    // "" and "/" mean the whole machine. Unix has one filesystem root, so that is simply a node
    // rooted at "/" and nothing else changes. Windows has no single root, so the node grows a volume
    // tier instead: the first path segment names the volume ("c/Users/mike" is "C:\Users\mike").
    private readonly bool _volumeTiered = IsWholeMachine(rootPath) && OperatingSystem.IsWindows();

    private readonly string _root = ResolveRoot(rootPath);

    // _root with a trailing separator, so a containment check lands on a segment boundary: without
    // it, root "/data/app" would accept "/data/apple/secret" as being inside itself.
    private readonly string _rootPrefix = WithTrailingSeparator(ResolveRoot(rootPath));

    /// <summary>Whether this node is rooted at the whole machine rather than one directory.</summary>
    public bool IsWholeMachineRoot => _volumeTiered || _root == "/";

    private static bool IsWholeMachine(string rootPath)
        => string.IsNullOrEmpty(rootPath) || rootPath == "/" || rootPath == "\\";

    private static string ResolveRoot(string rootPath)
        => IsWholeMachine(rootPath)
            ? (OperatingSystem.IsWindows() ? string.Empty : "/")   // empty: the volume tier has no single root
            : Path.GetFullPath(rootPath);

    // Windows and macOS are case-insensitive by default, everything else sensitive - a guess the
    // caller can override, since only the filesystem really knows. One value drives all three places
    // that need it: the search-pattern matcher, the containment check, and the host-path mapping.
    private readonly bool _caseSensitive = caseSensitive
        ?? !(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    /// <inheritdoc/>
    protected override bool IsCaseSensitive => _caseSensitive;

    private StringComparison PathComparison
        => _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string WithTrailingSeparator(string path)
        => path.Length == 0 || path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    // The OS bin for the running platform. Swappable so tests can drive the delete paths without
    // putting anything in the developer's real Trash.
    internal ITrashProvider TrashProvider { get; init; } = TrashProviders.Current;

    /// <summary>
    /// Creates a node from mount options. Activated by
    /// <c>MountSingleton</c>/<c>Scoped</c>/<c>Transient&lt;LocalFsNode&gt;</c> from the configured options.
    /// </summary>
    public LocalFsNode(VfsMountOptions options)
        : this(options.Require<LocalFsOptions>().RootPath, options.Require<LocalFsOptions>().CaseSensitive) { }

    /// <inheritdoc/>
    public override Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var physical = Resolve(request);
        if (!File.Exists(physical)) return Task.FromResult<Stream?>(null);
        Stream stream = new FileStream(physical, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 4096, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    /// <inheritdoc/>
    public override Task<Stream> OpenWriteAsync(VfsNodeRequest request, VfsWriteOptions? options = null, CancellationToken ct = default)
    {
        options ??= VfsWriteOptions.Default;

        var physical = Resolve(request);
        EnsureDirectory(physical);
        var fileMode = options.Mode switch
        {
            VfsWriteMode.Append    => FileMode.Append,
            VfsWriteMode.CreateNew => FileMode.CreateNew,
            _                      => FileMode.Create,
        };

        // Only pay for the stamping subclass when timestamps were actually asked for.
        Stream stream = options.ModifiedAt is null && options.CreatedAt is null
            ? new FileStream(physical, fileMode, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: true)
            : new TimestampedFileStream(physical, fileMode, options);

        return Task.FromResult(stream);
    }

    // Applies the caller's requested timestamps once the handle is closed. Setting them while the
    // stream is still open would be undone by the final flush, so it has to happen after the base
    // dispose. Subclassing FileStream rather than wrapping it keeps every Stream member native.
    private sealed class TimestampedFileStream : FileStream
    {
        private readonly string          _path;
        private readonly VfsWriteOptions _options;
        private          bool            _stamped;

        public TimestampedFileStream(string path, FileMode mode, VfsWriteOptions options)
            : base(path, mode, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true)
        {
            _path    = path;
            _options = options;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Stamp();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            Stamp();
        }

        private void Stamp()
        {
            if (_stamped) return;
            _stamped = true;
            try
            {
                if (_options.ModifiedAt is { } modified)
                    File.SetLastWriteTimeUtc(_path, modified.UtcDateTime);

                // Linux has no settable birth time and the runtime throws rather than ignoring it.
                if (_options.CreatedAt is { } created && !OperatingSystem.IsLinux())
                    File.SetCreationTimeUtc(_path, created.UtcDateTime);
            }
            catch (Exception ex) when (ex is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException)
            {
                // Best effort - the bytes are written and that is what the caller was promised.
            }
        }
    }

    /// <inheritdoc/>
    public override Task DeleteAsync(
        VfsNodeRequest request, VfsDeleteOptions? options = null, CancellationToken ct = default)
    {
        options ??= VfsDeleteOptions.Default;

        var physical = Resolve(request);
        var isFile   = File.Exists(physical);
        var isDir    = !isFile && Directory.Exists(physical);
        if (!isFile && !isDir) return Task.CompletedTask;   // already gone

        // Throws for a strict Recycle the volume cannot honour, rather than letting the shell quietly
        // destroy what the caller asked to keep.
        if (options.ResolveRecycle(TrashProvider.CanRecycle(physical), request.Path))
        {
            try
            {
                TrashProvider.Trash(physical);
                return Task.CompletedTask;
            }
            catch (Exception ex) when (options.Disposition == VfsDeleteDisposition.RecycleIfAvailable
                                       && ex is IOException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
            {
                // CanRecycle only vets the volume. A read-only mount, a bin over quota, or a trash
                // directory we cannot create only surface here - and falling through to a permanent
                // delete is exactly what RecycleIfAvailable asked for.
            }
        }

        if (isFile) File.Delete(physical);
        else        Directory.Delete(physical, options.Recursive);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        var pSrc = Resolve(src);
        var pDst = Resolve(dst);
        EnsureDirectory(pDst);
        File.Copy(pSrc, pDst, overwrite: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        var pSrc = Resolve(src);
        var pDst = Resolve(dst);
        EnsureDirectory(pDst);
        if      (File.Exists(pSrc))      File.Move(pSrc, pDst, overwrite: true);
        else if (Directory.Exists(pSrc)) Directory.Move(pSrc, pDst);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Native listing: recursion, search pattern, kind, and hidden filtering all resolve in a
    /// single OS-level enumeration. Attributes/size/timestamps come straight off the walk (no
    /// per-entry re-stat), and the pattern is matched by the runtime's own simple-glob matcher.
    /// The base <see cref="VfsNodeBase"/> engine (over <see cref="ListDirectoryAsync"/>) remains the
    /// fallback for backends that can't push these down.
    /// </summary>
    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, VfsListOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= VfsListOptions.Default;

        if (AtVolumeTierRoot(request))
        {
            foreach (var volume in VolumeEntries(options)) yield return volume;
            yield break;
        }

        var physical = Resolve(request);
        if (!Directory.Exists(physical)) yield break;

        var pattern       = string.IsNullOrEmpty(options.SearchPattern) ? "*" : options.SearchPattern!;
        var includeHidden = options.IncludeHidden;
        var kind          = options.Kind;
        var caseSensitiveMatch = _caseSensitive;   // captured: the predicate is a static-context lambda

        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = options.Recurse,
            // Our MaxDepth counts levels below the listed dir (immediate children = 1); .NET's
            // MaxRecursionDepth counts descents (0 = top directory only). So subtract one.
            MaxRecursionDepth  = options.MaxDepth is { } md ? Math.Max(0, md - 1) : int.MaxValue,
            AttributesToSkip   = 0,      // filter hidden in the predicate so recursion still descends
            IgnoreInaccessible = true,
        };

        var entries = new FileSystemEnumerable<VfsNodeInfo>(
            physical,
            (ref FileSystemEntry e) => BuildInfoFromEntry(ref e),
            enumOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
            {
                var kindOk = e.IsDirectory
                    ? (kind & VfsEntryKind.Directories) != 0
                    : (kind & VfsEntryKind.Files) != 0;
                if (!kindOk) return false;
                if (!includeHidden && (e.Attributes & FileAttributes.Hidden) != 0) return false;
                return pattern == "*"
                    || FileSystemName.MatchesSimpleExpression(pattern, e.FileName, ignoreCase: !caseSensitiveMatch);
            },
        };

        foreach (var info in entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return info;
        }
    }

    /// <summary>
    /// Single-level primitive: satisfies the base contract and backs any caller that reaches
    /// for it, though LocalFs's own listing goes through the native <see cref="ListAsync"/> above.
    /// </summary>
    protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(
        VfsNodeRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (AtVolumeTierRoot(request))
        {
            foreach (var volume in VolumeEntries(VfsListOptions.Default)) yield return volume;
            yield break;
        }

        var physical = Resolve(request);
        if (!Directory.Exists(physical)) yield break;

        foreach (var entry in Directory.EnumerateFileSystemEntries(physical))
        {
            ct.ThrowIfCancellationRequested();
            var info = BuildInfo(entry, request.Path);
            if (info is not null) yield return info;
        }
    }

    private VfsNodeInfo BuildInfoFromEntry(ref FileSystemEntry entry)
    {
        // An OS symlink or junction is a reparse point. Report it as the kind it is, so a listing says
        // so the way readdir's DT_LNK and Windows' reparse attribute both do, and keep the legacy
        // property for anyone already reading it.
        var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        var props  = ImmutableDictionary<string, string?>.Empty;
        if (isLink) props = props.Add(VfsPropertyKeys.PhysicalSymlink, "true");

        // One materialisation, used three ways - the relative path is a slice of it, and it is
        // what LocalPath reports. It was already being built here (twice, for a link) and discarded.
        var fullPath = entry.ToFullPath();
        var target   = isLink ? SafeLinkTarget(fullPath) : null;

        return new VfsNodeInfo
        {
            RelativePath = BuildRelativePath(fullPath),
            LocalPath    = fullPath,
            IsFile       = !entry.IsDirectory,
            IsDirectory  = entry.IsDirectory,
            IsSymlink    = isLink,
            SymlinkTarget = target,
            IsHidden     = (entry.Attributes & FileAttributes.Hidden) != 0,
            SizeBytes    = entry.IsDirectory ? null : entry.Length,
            CreatedAt    = entry.CreationTimeUtc,
            ModifiedAt   = entry.LastWriteTimeUtc,
            AccessedAt   = entry.LastAccessTimeUtc,
            Properties   = props,
        };
    }

    /// <inheritdoc/>
    public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default)
        => Task.FromResult(GetInfoInternal(request));

    /// <inheritdoc/>
    public override Task<bool> ExistsAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        if (AtVolumeTierRoot(request)) return Task.FromResult(true);

        var physical = Resolve(request);
        return Task.FromResult(File.Exists(physical) || Directory.Exists(physical));
    }

    // -- Helpers ---------------------------------------------------------------

    // Resolve a VfsNodeRequest to a physical path.
    // ADS: if StreamName is present, appends ":streamName" to the path.
    // FileStream on Windows supports ADS natively. On Linux this will fail at the OS level.
    private string Resolve(VfsNodeRequest request)
    {
        var combined = ToHostPath(request.Path)
            ?? throw new DirectoryNotFoundException(
                $"'{request.Path}' does not name a volume on this machine.");

        if (!IsInsideRoot(combined))
            throw new UnauthorizedAccessException(
                $"Path traversal denied: '{request.Path}' escapes mount root '{_root}'.");

        var streamSpan = request.Path.StreamSpan;
        return streamSpan.IsEmpty
            ? combined
            : $"{combined}:{new string(streamSpan)}";
    }

    private VfsNodeInfo? GetInfoInternal(VfsNodeRequest request)
    {
        // The volume tier is a real place with no host path of its own - it is the set of volumes.
        if (AtVolumeTierRoot(request))
            return new VfsNodeInfo { RelativePath = default, IsFile = false, IsDirectory = true };

        var physical = Resolve(request);

        if (File.Exists(physical))
        {
            var fi     = new FileInfo(physical);
            var isLink = (fi.Attributes & FileAttributes.ReparsePoint) != 0;
            var props  = ImmutableDictionary<string, string?>.Empty;
            if (isLink) props = props.Add(VfsPropertyKeys.PhysicalSymlink, "true");

            return new VfsNodeInfo
            {
                RelativePath = BuildRelativePath(fi.FullName),
                LocalPath    = fi.FullName,
                IsFile       = true,
                IsDirectory  = false,
                IsSymlink    = isLink,
                SymlinkTarget = isLink ? SafeLinkTarget(physical) : null,
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
                LocalPath    = di.FullName,
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
        if (_volumeTiered)
            return LocalFsVolumes.TryToRelative(physicalAbsolute, out var v) ? VfsPath.From(v) : default;

        var rel = physicalAbsolute[_root.Length..]
            .TrimStart(Path.DirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/');
        return VfsPath.From(rel);
    }

    // Mount-relative VFS path -> absolute host path, or null when there is nowhere for it to be.
    // No containment check: callers decide whether an escape throws (an operation) or answers null.
    private string? ToHostPath(VfsPath relativePath)
    {
        if (_volumeTiered) return LocalFsVolumes.ToHostPath(relativePath.PathSpan);

        var relSpan = relativePath.PathSpan;
        Span<char> buf = stackalloc char[relSpan.Length + 1];
        for (int i = 0; i < relSpan.Length; i++)
            buf[i] = relSpan[i] == '/' ? Path.DirectorySeparatorChar : relSpan[i];
        var rel = new string(buf[..relSpan.Length]);
        return Path.GetFullPath(Path.Combine(_root, rel));
    }

    // -- ILocalPathMapping -----------------------------------------------------

    /// <inheritdoc/>
    public override T? GetNodeCapability<T>(VfsPath mountPoint) where T : class
        => this as T;

    /// <inheritdoc/>
    public string? ToLocalPath(VfsPath relativePath)
    {
        var combined = ToHostPath(relativePath);
        return combined is not null && IsInsideRoot(combined) ? combined : null;
    }

    /// <inheritdoc/>
    public bool TryGetRelativePath(string localPath, out VfsPath relativePath)
    {
        relativePath = default;
        if (string.IsNullOrEmpty(localPath)) return false;

        // An extended-length prefix is stripped first: a picker hands "\\?\C:\..." back for a long
        // path, and nothing downstream understands it.
        var candidate = LocalFsVolumes.StripExtendedPrefix(localPath);

        // Only a fully qualified path names a place. Anything else - "docs/x", "\docs\x", "C:docs" -
        // would be completed from the process's working directory or current drive, and that is
        // ambient state, not a location. Refuse rather than guess: false here means "not ours", and
        // it is not anyone's.
        if (!Path.IsPathFullyQualified(candidate)) return false;

        if (_volumeTiered)
        {
            if (!LocalFsVolumes.TryToRelative(candidate, out var volumeRelative)) return false;
            relativePath = VfsPath.From(volumeRelative);
            return true;
        }

        // GetFullPath on a fully qualified path resolves "." and ".." and mixed separators without
        // touching the working directory, so the comparison below is against a canonical path.
        string full;
        try   { full = Path.GetFullPath(candidate); }
        catch { return false; }                        // malformed - not ours

        if (!IsInsideRoot(full)) return false;

        relativePath = full.Length == _root.Length ? default : BuildRelativePath(full);
        return true;
    }

    // The mount root of a volume-tiered node - "/local" itself, which names the machine rather than
    // any directory on it.
    private bool AtVolumeTierRoot(VfsNodeRequest request)
        => _volumeTiered && request.Path.PathSpan.IsEmpty;

    // The volumes, as the children of the tier root. Not recursed into: descending from here would
    // walk every volume on the machine, which is never what a caller listing a root meant to ask for.
    private static IEnumerable<VfsNodeInfo> VolumeEntries(VfsListOptions options)
    {
        if ((options.Kind & VfsEntryKind.Directories) == 0) yield break;

        foreach (var segment in LocalFsVolumes.EnumerateVolumeSegments())
            yield return new VfsNodeInfo
            {
                RelativePath = VfsPath.From(segment),
                IsFile       = false,
                IsDirectory  = true,
            };
    }

    // True when an absolute host path is the root itself or sits beneath it, on a segment boundary.
    private bool IsInsideRoot(string absolute)
        => _volumeTiered                                    // the volume translation is the check
        || absolute.Equals(_root, PathComparison)
        || absolute.StartsWith(_rootPrefix, PathComparison);

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    // The OS link target, or null when it cannot be read. Best-effort: a broken or inaccessible link
    // must still list, and reporting it as a link with an unknown target beats failing the listing.
    private static string? SafeLinkTarget(string physicalPath)
    {
        try { return File.ResolveLinkTarget(physicalPath, returnFinalTarget: false)?.FullName; }
        catch (IOException)                   { return null; }
        catch (UnauthorizedAccessException)   { return null; }
    }

    // -- Content hashes --------------------------------------------------------

    /// <summary>Hashing and recycle-bin questions, each bound to the entry asked for.</summary>
    public override T? GetEntryCapability<T>(VfsPath relativePath) where T : class
        => typeof(T) == typeof(IRecycling)      ? new LocalFsRecycling(this, relativePath)      as T
         : typeof(T) == typeof(IContentHashing) ? new LocalFsEntryHashing(this, relativePath)   as T
         : null;

    // Serves LocalFsRecycling. Resolving here rather than in the capability keeps the mount-root
    // traversal guard on the one path that knows about it.
    internal bool CanRecycle(VfsPath path)
        => TrashProvider.CanRecycle(Resolve(new VfsNodeRequest(path)));

    // Serves LocalFsEntryHashing. Local disk is the one backend where computing is cheap enough to
    // just do, so there is nothing to report natively and nothing to refuse.
    internal async Task<string?> ComputeHashAsync(
        VfsPath path, string algorithm, CancellationToken ct)
    {
        var stream = await OpenReadAsync(new VfsNodeRequest(path), ct);
        if (stream is null) return null;

        await using (stream)
            return await VfsHashing.ComputeAsync(stream, algorithm, ct);
    }
}

/// <summary>
/// A <see cref="LocalFsNode"/>'s recycle-bin availability, bound to one entry.
/// </summary>
/// <remarks>
/// The answer is per-entry rather than per-node because a mount can span volumes: a directory tree
/// with mount points under it, or on Windows a UNC path sitting next to a fixed disk, where one has a
/// bin and the other does not.
/// </remarks>
internal sealed class LocalFsRecycling(LocalFsNode node, VfsPath path) : IRecycling
{
    /// <inheritdoc/>
    public ValueTask<bool> CanRecycleAsync(CancellationToken ct = default)
        => ValueTask.FromResult(node.CanRecycle(path));
}

/// <summary>
/// A <see cref="LocalFsNode"/>'s hashing bound to one entry.
/// </summary>
internal sealed class LocalFsEntryHashing(LocalFsNode node, VfsPath path) : IContentHashing
{
    /// <summary>
    /// Empty. A local filesystem stores no content hash, so there is never a free answer - unlike a
    /// remote store, where the backend has usually already computed one.
    /// </summary>
    public IReadOnlyList<string> NativeAlgorithms { get; } = [];

    /// <summary>
    /// The standard algorithms. Computing means reading the file, which on local disk is cheap enough
    /// to be worth offering - and it is what makes a local side comparable with a remote one.
    /// </summary>
    public IReadOnlyList<string> ComputableAlgorithms { get; } = VfsHashing.Computable;

    /// <inheritdoc/>
    public Task<string?> GetHashAsync(
        string algorithm, VfsHashBudget budget = VfsHashBudget.Fetch, CancellationToken ct = default)
        // Every answer here reads the file, and there is nowhere to store one - a local filesystem has
        // no metadata slot for it - so ComputeAndStore does exactly what Compute does.
        => budget < VfsHashBudget.Compute
            ? Task.FromResult<string?>(null)
            : node.ComputeHashAsync(path, algorithm, ct);
}
