using System.Runtime.CompilerServices;
using Dytools.VirtualFileSystem.Nodes.LocalFs;
using System.Text.Json;

namespace Dytools.VirtualFileSystem.Internal;


internal sealed class DefaultVirtualFileSystem : IVirtualFileSystem, IDisposable
{
    private readonly IVfsMountRegistry _sharedRegistry;
    private readonly VfsPipeline       _pipeline;
    private readonly IServiceProvider? _ambient;            // the scope this instance was resolved from
    private readonly VfsPath?          _currentDirectory;
    private          VfsMountRegistry? _localRegistry;
    private          bool              _disposed;

    public DefaultVirtualFileSystem(
        IVfsMountRegistry registry,
        VfsPipeline pipeline,
        IServiceProvider? ambient = null,
        string? currentDirectory = null)
    {
        _sharedRegistry   = registry;
        _pipeline         = pipeline;
        _ambient          = ambient;
        _currentDirectory = currentDirectory is null ? null : VfsPath.From(currentDirectory);
    }

    public DefaultVirtualFileSystem(
        IVfsMountRegistry registry,
        VfsPipeline pipeline,
        IServiceProvider? ambient,
        VfsPath currentDirectory)
    {
        _sharedRegistry   = registry;
        _pipeline         = pipeline;
        _ambient          = ambient;
        _currentDirectory = currentDirectory;
    }

    public string? CurrentDirectory => _currentDirectory?.ToString();

    // Active registry: local layer (if any instance mounts exist) or the shared registry.
    private IVfsMountRegistry ActiveRegistry => (IVfsMountRegistry?)_localRegistry ?? _sharedRegistry;

    // Lazily creates the local registry on first Mount/Alias call.
    private VfsMountRegistry LocalRegistry => _localRegistry ??= new VfsMountRegistry(_sharedRegistry);

    // -- Scoping ---------------------------------------------------------------

    public IVirtualFileSystem ScopeTo(string path)
    {
        var resolvedPath = VfsPath.From(path, _currentDirectory ?? _root);
        return new DefaultVirtualFileSystem(ActiveRegistry, _pipeline, _ambient, resolvedPath);
    }

    // -- Instance mounting -----------------------------------------------------

    public void Mount(string mountPoint, IVfsNode node)
    {
        ThrowIfDisposed();
        LocalRegistry.Mount(mountPoint, node);
    }

    public void Unmount(string mountPoint)
    {
        ThrowIfDisposed();
        _localRegistry?.Unmount(mountPoint);
    }

    // -- Host filesystem interop -----------------------------------------------

    public bool TryGetVfsPath(string localPath, out string vfsPath)
    {
        var candidates = GetVfsPathCandidates(localPath);
        vfsPath = candidates.Count == 0 ? string.Empty : candidates[0].Path;
        return candidates.Count != 0;
    }

    public IReadOnlyList<VfsPathCandidate> GetVfsPathCandidates(
        string localPath, VfsPathLookupOptions? options = null)
    {
        var opts = options ?? VfsPathLookupOptions.Default;

        // A relative path is a caller error, not a lookup miss: no mount can ever cover it, so a
        // "false - mount it and retry" answer would send the caller the wrong way. The rule is the
        // host OS's own - a drive-rooted or UNC path on Windows, a "/"-rooted one elsewhere - and the
        // process's working directory and current drive never take part.
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        if (!Path.IsPathFullyQualified(LocalFsVolumes.StripExtendedPrefix(localPath)))
            throw new ArgumentException(
                $"'{localPath}' is not a fully qualified host path. " +
                (OperatingSystem.IsWindows()
                    ? @"On Windows a drive-rooted path (C:\...) or a UNC path (\\server\share\...) is required; "
                    : "A path starting with '/' is required; ") +
                "relative and drive-relative paths are never resolved against the working directory.",
                nameof(localPath));

        var aliases = opts.IncludeAliases
            ? ActiveRegistry.EnumerateAliases().ToArray()
            : [];

        var found = new List<(int Rank, VfsPathCandidate Candidate)>();

        foreach (var (mountPoint, node, isInternal) in ActiveRegistry.EnumerateMounts(_ambient))
        {
            var mapping = node.GetNodeCapability<ILocalPathMapping>(mountPoint);
            if (mapping is null || !mapping.TryGetRelativePath(localPath, out var relative)) continue;

            var full     = relative.PathSpan.IsEmpty ? mountPoint : VfsPath.From(mountPoint, relative);
            var mountStr = mountPoint.ToString();

            // A mount rooted deeper in the host filesystem leaves a shorter relative path, so
            // shortest-relative is the most specific mount - the rule Resolve uses, run backwards.
            var rank = relative.PathSpan.Length;

            // The direct path to an internal mount throws when used, so it is not offered unless it
            // was asked for. Alias routes to the same mount stay, because those do work.
            if (!isInternal || opts.IncludeInternal)
                found.Add((rank, new VfsPathCandidate
                {
                    Path       = full.ToString(),
                    MountPoint = mountStr,
                    IsInternal = isInternal,
                }));

            foreach (var (alias, target, aliasInternal) in aliases)
            {
                if (aliasInternal) continue;          // an internal alias sanctions nothing
                if (!full.StartsWith(target)) continue;

                found.Add((rank, new VfsPathCandidate
                {
                    Path       = VfsPath.Rebase(full, target, alias).ToString(),
                    MountPoint = mountStr,
                    ViaAlias   = alias.ToString(),
                    IsInternal = isInternal,
                }));
            }
        }

        // OrderBy is stable, so within one mount the direct path stays ahead of its alias routes.
        return found.OrderBy(static x => x.Rank).Select(static x => x.Candidate).ToList();
    }

    // -- Streams ---------------------------------------------------------------

    // Seekability is applied here rather than in a node because no backend can do it better: a
    // forward-only HTTP body is forward-only however it is asked for. A node that already hands back
    // a seekable stream (LocalFs) is left alone, so the guarantee costs nothing where it is free.
    public async Task<Stream?> OpenReadAsync(string path, VfsReadOptions? options, CancellationToken ct = default)
    {
        var opts   = options ?? VfsReadOptions.Default;
        var stream = await _pipeline.ExecuteReadAsync(Ctx(path), opts, ct);

        if (stream is null || !opts.Seekable || stream.CanSeek) return stream;
        return new SeekableReadStream(stream, ownsInner: true, opts.MemoryThreshold);
    }

    public Task<Stream> OpenWriteAsync(string path, VfsWriteOptions? options = null, CancellationToken ct = default)
        => _pipeline.ExecuteWriteAsync(Ctx(path), options ?? VfsWriteOptions.Default, ct);

    // -- Copy / Move / Rename / Delete -----------------------------------------

    public Task CopyAsync(string src, string dst, CancellationToken ct = default)
        => _pipeline.ExecuteCopyAsync(Ctx(src), Ctx(dst), ct);

    public Task MoveAsync(string src, string dst, CancellationToken ct = default)
        => _pipeline.ExecuteMoveAsync(Ctx(src), Ctx(dst), ct);

    public Task RenameAsync(string path, string newName, CancellationToken ct = default)
        => _pipeline.ExecuteRenameAsync(Ctx(path), newName, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default)
        => DeleteAsync(path, null, ct);

    public Task DeleteAsync(string path, VfsDeleteOptions? options, CancellationToken ct = default)
        => _pipeline.ExecuteDeleteAsync(Ctx(path), options ?? VfsDeleteOptions.Default, ct);

    // -- Metadata --------------------------------------------------------------

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => ExistsAsync(path, null, ct);

    public Task<bool> ExistsAsync(string path, VfsMetadataOptions? options, CancellationToken ct = default)
        => _pipeline.ExecuteExistsAsync(Ctx(path), options ?? VfsMetadataOptions.Default, ct);

    public Task<VfsEntryInfo?> GetInfoAsync(string path, CancellationToken ct = default)
        => GetInfoAsync(path, null, ct);

    public async Task<VfsEntryInfo?> GetInfoAsync(
        string path, VfsMetadataOptions? options, CancellationToken ct = default)
    {
        var ctx  = Ctx(path);
        var info = await _pipeline.ExecuteGetInfoAsync(ctx, options ?? VfsMetadataOptions.Default, ct);
        return info is null ? null : Enrich(info, ctx);
    }

    public IAsyncEnumerable<string> ListAsync(string path, CancellationToken ct = default)
        => ListAsync(path, VfsListOptions.Default, ct);

    public async IAsyncEnumerable<string> ListAsync(
        string path, VfsListOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ctx = Ctx(path);
        await foreach (var entry in _pipeline.ExecuteListAsync(ctx, options, ct))
            yield return VfsPath.From(ctx.MountPoint, entry.RelativePath).ToString(); // TODO alias respected or always return actual mount point?
    }

    public IAsyncEnumerable<VfsEntryInfo> ListInfoAsync(string path, CancellationToken ct = default)
        => ListInfoAsync(path, VfsListOptions.Default, ct);

    public async IAsyncEnumerable<VfsEntryInfo> ListInfoAsync(
        string path, VfsListOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ctx = Ctx(path);

        // Ask once whether anything below this directory could route a child elsewhere. Almost always
        // nothing does, and then every entry belongs to the mount already resolved for the directory -
        // so re-resolving each one would be a full alias scan and mount scan per entry to arrive back
        // at the answer in hand. Only when something can shadow is the per-entry work worth doing.
        var mayShadow = ActiveRegistry.HasShadowingUnder(ctx.Path, ctx.ResolvedPath);

        await foreach (var nodeInfo in _pipeline.ExecuteListAsync(ctx, options, ct))
        {
            if (!mayShadow)
            {
                yield return Enrich(nodeInfo, ctx.MountPoint);
                continue;
            }

            var childCtx = new VfsContext(
                VfsPath.From(ctx.MountPoint, nodeInfo.RelativePath),
                ActiveRegistry, _ambient);
            yield return Enrich(nodeInfo, childCtx);
        }
    }

    // -- Consumer capability query ---------------------------------------------

    public T? GetEntryCapability<T>(string path) where T : class, IEntryCapability
    {
        var ctx = Ctx(path);
        return ctx.ResolvedNode.GetEntryCapability<T>(ctx.BuildNodeRequest().Path);
    }

    public T? GetNodeCapability<T>(string path) where T : class, INodeCapability
    {
        var ctx = Ctx(path);
        return ctx.ResolvedNode.GetNodeCapability<T>(ctx.MountPoint);
    }

    // -- Internals -------------------------------------------------------------

    // VFS API boundary: relative paths must be anchored to an absolute base before
    // entering the registry. CurrentDirectory is used when set; otherwise the root.
    private static readonly VfsPath _root = VfsPath.From("/");

    private VfsContext Ctx(string path)
        => new(VfsPath.From(path, _currentDirectory ?? _root), ActiveRegistry, _ambient);

    // Compose VfsNodeInfo (node-relative, node-known) into VfsEntryInfo (VFS-canonical).
    // The node's RelativePath gives us correct storage casing.
    // IsAliased and IsSymlink are VFS-layer facts - nodes never set them.
    private static VfsEntryInfo Enrich(VfsNodeInfo info, VfsContext ctx)
        => Enrich(info, ctx.MountPoint,
                  ctx.RawItems?.ContainsKey(VfsContextKeys.AliasFollowed)   == true,
                  ctx.RawItems?.ContainsKey(VfsContextKeys.SymlinkFollowed) == true);

    // Overload for listing entries that cannot be shadowed: the mount is the directory's own, and
    // neither flag can be set, because nothing was re-routed to reach the entry.
    private static VfsEntryInfo Enrich(VfsNodeInfo info, VfsPath mountPoint)
        => Enrich(info, mountPoint, aliasFollowed: false, symlinkFollowed: false);

    private static VfsEntryInfo Enrich(
        VfsNodeInfo info, VfsPath mountPoint, bool aliasFollowed, bool symlinkFollowed)
    {
        // Build canonical VFS path: mount + "/" + node-relative (correct casing).
        var fullPath  = VfsPath.From(mountPoint, info.RelativePath);
        var vfsPath   = fullPath.ToString();

        var name = info.RelativePath.GetName();

        return new VfsEntryInfo
        {
            Path        = vfsPath,
            Name        = name,
            IsFile      = info.IsFile,
            IsDirectory = info.IsDirectory,
            IsHidden    = info.IsHidden,
            IsAliased       = aliasFollowed,          // alias store was traversed
            IsSymlink       = info.IsSymlink,         // node-reported kind
            SymlinkTarget   = info.SymlinkTarget,
            FollowedSymlink = symlinkFollowed,        // a symlink was followed to get here
            LocalPath   = info.LocalPath,             // node-reported; null unless it has one
            CreatedAt   = info.CreatedAt,
            ModifiedAt  = info.ModifiedAt,
            AccessedAt  = info.AccessedAt,
            SizeBytes   = info.SizeBytes,
            Properties  = info.Properties,
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(IVirtualFileSystem));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    // Also IDisposable so DI scopes disposed synchronously (console, background services,
    // `using var scope = ...`) can release this instance without requiring async disposal.
    public void Dispose() => _disposed = true;
}
