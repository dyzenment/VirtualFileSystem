using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem.Internal;

// How a mount produces its node: a fixed instance (runtime vfs.Mount), or a keyed DI
// service (builder mounts) whose lifetime - singleton / scoped / transient - the DI
// container owns. Keyed resolution happens against the caller's ambient scope, so a
// scoped-mounted node comes from the request scope.
internal readonly struct MountEntry
{
    private readonly IVfsNode? _instance;
    private readonly string?   _diKey;

    private MountEntry(IVfsNode? instance, string? diKey)
    {
        _instance = instance;
        _diKey    = diKey;
    }

    public static MountEntry ForInstance(IVfsNode node) => new(node, null);
    public static MountEntry ForKey(string diKey)       => new(null, diKey);

    public IVfsNode Resolve(IServiceProvider? provider)
    {
        if (_instance is not null) return _instance;
        if (provider is null)
            throw new InvalidOperationException(
                "This mount resolves its node from DI, but no service provider is available. " +
                "Resolve IVirtualFileSystem from a DI scope so the node can be built.");
        return provider.GetRequiredKeyedService<IVfsNode>(_diKey);
    }
}

// Mount and alias tables are immutable sorted arrays swapped atomically on write.
// Resolve is lock-free: volatile reads + struct enumerators, zero heap allocation on
// the common path. Mounts are sorted descending by key length so the first prefix hit
// is the longest match.
internal sealed class VfsMountRegistry : IVfsMountRegistry
{
    private readonly IVfsMountRegistry? _parent;    // set on per-instance child registries
    private readonly IServiceProvider?  _provider;  // root's fallback provider for keyed resolution

    public VfsMountRegistry(IServiceProvider provider) => _provider = provider;
    internal VfsMountRegistry(IVfsMountRegistry parent) => _parent = parent;

    private volatile (VfsPath Key, MountEntry Entry, bool Internal)[] _mounts  = [];
    private volatile (VfsPath Alias, VfsPath Target, bool Internal)[] _aliases = [];
    private readonly object _writeLock = new();

    // -- IVfsMountRegistry -----------------------------------------------------

    // Runtime instance mount (IVirtualFileSystem.Mount / direct registry use) - public.
    public void Mount(string mountPoint, IVfsNode node)
        => AddMount(mountPoint, MountEntry.ForInstance(node), isInternal: false);

    // Keyed DI mount, driven by the builder at startup, with its internal flag.
    internal void MountKeyed(string mountPoint, bool isInternal)
        => AddMount(mountPoint, MountEntry.ForKey(mountPoint), isInternal);

    private void AddMount(string mountPoint, MountEntry entry, bool isInternal)
    {
        var key = VfsPath.From(mountPoint);
        lock (_writeLock)
        {
            var cur  = _mounts;
            var next = new List<(VfsPath Key, MountEntry Entry, bool Internal)>(cur.Length + 1);
            foreach (var e in cur)
                if (e.Key != key) next.Add(e);
            next.Add((key, entry, isInternal));
            next.Sort(static (a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _mounts = next.ToArray();
        }
    }

    public void Unmount(string mountPoint)
    {
        var key = VfsPath.From(mountPoint);
        lock (_writeLock)
        {
            var cur  = _mounts;
            var next = new List<(VfsPath Key, MountEntry Entry, bool Internal)>(cur.Length);
            foreach (var e in cur)
                if (e.Key != key) next.Add(e);
            _mounts = next.ToArray();
        }
    }

    public void Alias(string alias, string target, bool isInternal = false)
    {
        var a = VfsPath.From(alias);
        var t = VfsPath.From(target);
        lock (_writeLock)
        {
            var cur  = _aliases;
            var next = new List<(VfsPath Alias, VfsPath Target, bool Internal)>(cur.Length + 1);
            foreach (var e in cur)
                if (e.Alias != a) next.Add(e);
            next.Add((a, t, isInternal));
            _aliases = next.ToArray();
        }
    }

    public void RemoveAlias(string alias)
    {
        var a = VfsPath.From(alias);
        lock (_writeLock)
        {
            var cur  = _aliases;
            var next = new List<(VfsPath Alias, VfsPath Target, bool Internal)>(cur.Length);
            foreach (var e in cur)
                if (e.Alias != a) next.Add(e);
            _aliases = next.ToArray();
        }
    }

    // -- Resolve ---------------------------------------------------------------

    public (IVfsNode Node, VfsPath MountPoint, VfsPath ResolvedPath) Resolve(
        VfsPath path, IServiceProvider? serviceProvider = null, bool internalAllowed = false)
    {
        var aliasSnap = _aliases;
        var (expanded, firstAliasPublic) = ExpandAliases(path, aliasSnap, depth: 0);
        var isAliased = expanded is not null;
        var effective = isAliased ? expanded!.Value : path;

        var mountSnap = _mounts;
        VfsPath     matchKey      = default;
        MountEntry? matchEntry    = null;
        bool        matchInternal = false;

        foreach (var (key, entry, isInternal) in mountSnap)
        {
            if (!effective.StartsWith(key)) continue;
            matchKey      = key;
            matchEntry    = entry;
            matchInternal = isInternal;
            break;
        }

        if (matchEntry is null)
        {
            if (_parent is not null) return _parent.Resolve(path, serviceProvider, internalAllowed);
            throw new DirectoryNotFoundException($"No VFS mount found for path: {path}");
        }

        // Internal enforcement: an internal mount is reachable only when the access is
        // sanctioned - a reroute (internalAllowed), or through a public (non-internal) alias.
        if (matchInternal && !internalAllowed && !(isAliased && firstAliasPublic))
            throw new DirectoryNotFoundException(
                $"'{path}' resolves to an internal mount and is not directly accessible; " +
                "reach it via an alias or a decorator node.");

        VfsPath resolvedPath;
        if (!isAliased)
        {
            resolvedPath = path;
        }
        else
        {
            var streamSpan = path.StreamSpan;
            var querySpan  = path.QuerySpan;

            if (streamSpan.IsEmpty && querySpan.IsEmpty)
            {
                resolvedPath = effective;
            }
            else
            {
                var baseSpan = effective.PathSpan;
                Span<char> buf = stackalloc char[VfsPath.MaxLength];
                baseSpan.CopyTo(buf);
                int len = baseSpan.Length;
                if (!streamSpan.IsEmpty) { buf[len++] = ':'; streamSpan.CopyTo(buf[len..]); len += streamSpan.Length; }
                if (!querySpan.IsEmpty)  { buf[len++] = '?'; querySpan.CopyTo(buf[len..]); len += querySpan.Length; }
                resolvedPath = VfsPath.From(buf[..len], path.IsCaseSensitive);
            }
        }

        var node = matchEntry.Value.Resolve(serviceProvider ?? _provider);
        return (node, matchKey, resolvedPath);
    }

    // -- Alias expansion -------------------------------------------------------

    // Returns the expanded path (null if no alias matched) and whether the FIRST alias the
    // consumer's path hit was public - that first hop is the door, so its public-ness decides
    // whether reaching an internal target is sanctioned.
    private static (VfsPath? Expanded, bool FirstAliasPublic) ExpandAliases(
        VfsPath path, (VfsPath Alias, VfsPath Target, bool Internal)[] aliases, int depth)
    {
        if (depth > 20)
            throw new InvalidOperationException(
                $"VFS alias depth limit exceeded at depth {depth}: {path}");

        foreach (var (alias, target, isInternal) in aliases)
        {
            if (!path.StartsWith(alias)) continue;

            var expanded    = VfsPath.Rebase(path, alias, target);
            var (deeper, _) = ExpandAliases(expanded, aliases, depth + 1);
            return (deeper ?? expanded, !isInternal);
        }

        return (null, false);
    }
}
