namespace Dytools.VirtualFileSystem.Internal;

// Mount and alias tables are stored as immutable sorted arrays replaced atomically
// on every write (Interlocked / volatile swap inside _writeLock).
//
// Read path (Resolve - called on every VFS operation):
//   • volatile array read  - one memory-barrier instruction, no lock
//   • struct array enumerator - zero heap allocation
//   • early break on first match (array is sorted descending by key length,
//     so the first prefix hit is always the longest-prefix match)
//
// Write path (Mount / Unmount / Alias - startup-time or rare dynamic use):
//   • serialised by _writeLock so concurrent writers don't race
//   • builds a new array from scratch, then assigns via volatile write
//   • allocates freely - correctness and simplicity matter more than speed here
internal sealed class VfsMountRegistry : IVfsMountRegistry
{
    private readonly IVfsMountRegistry? _parent;

    /// <summary>Creates a root registry with no parent (global singleton).</summary>
    public VfsMountRegistry() { }

    // Internal ctor - takes IVfsMountRegistry directly but is not discoverable by DI
    // (DI would see the IVfsMountRegistry parameter and cause a circular dependency).
    internal VfsMountRegistry(IVfsMountRegistry parent) => _parent = parent;

    // Immutable snapshots. volatile ensures every read sees the latest write
    // without a lock. The array contents are never mutated after assignment.
    // Mounts: sorted descending by key length - first prefix match = longest match.
    private volatile (VfsPath Key, IVfsNode Node)[]    _mounts  = [];
    private volatile (VfsPath Alias, VfsPath Target)[] _aliases = [];

    // Serialises concurrent writers. Readers never acquire this lock.
    private readonly object _writeLock = new();

    // -- IVfsMountRegistry -----------------------------------------------------

    public void Mount(string mountPoint, IVfsNode node)
    {
        var key = VfsPath.From(mountPoint);
        lock (_writeLock)
        {
            var cur  = _mounts;
            var next = new List<(VfsPath Key, IVfsNode Node)>(cur.Length + 1);
            foreach (var e in cur)
                if (e.Key != key) next.Add(e);
            next.Add((key, node));
            // Longest prefix must win → sort descending so we can break on first match.
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
            var next = new List<(VfsPath Key, IVfsNode Node)>(cur.Length);
            foreach (var e in cur)
                if (e.Key != key) next.Add(e);
            // Removing an element from a sorted array keeps it sorted - no re-sort needed.
            _mounts = next.ToArray();
        }
    }

    public void Alias(string alias, string target)
    {
        var a = VfsPath.From(alias);
        var t = VfsPath.From(target);
        lock (_writeLock)
        {
            var cur  = _aliases;
            var next = new List<(VfsPath Alias, VfsPath Target)>(cur.Length + 1);
            foreach (var e in cur)
                if (e.Alias != a) next.Add(e);
            next.Add((a, t));
            _aliases = next.ToArray();
        }
    }

    public void RemoveAlias(string alias)
    {
        var a = VfsPath.From(alias);
        lock (_writeLock)
        {
            var cur  = _aliases;
            var next = new List<(VfsPath Alias, VfsPath Target)>(cur.Length);
            foreach (var e in cur)
                if (e.Alias != a) next.Add(e);
            _aliases = next.ToArray();
        }
    }

    // -- Resolve ---------------------------------------------------------------

    // Lock-free hot path. Volatile reads + struct enumerators = zero heap allocation
    // for the common case (no alias, clean absolute path).
    public (IVfsNode Node, VfsPath MountPoint, VfsPath ResolvedPath) Resolve(VfsPath path)
    {
        var aliasSnap = _aliases;               // one volatile read, captured for the call
        var expanded  = ExpandAliases(path, aliasSnap, depth: 0);
        var isAliased = expanded is not null;
        var effective = isAliased ? expanded!.Value : path;

        // Struct enumerator - zero alloc. Sorted descending: break on first valid match.
        var mountSnap = _mounts;                // one volatile read
        VfsPath   matchKey  = default;
        IVfsNode? matchNode = null;

        foreach (var (key, node) in mountSnap)
        {
            if (!effective.StartsWith(key)) continue;
            matchKey  = key;
            matchNode = node;
            break; // first match IS the longest - done
        }

        if (matchNode is null)
        {
            if (_parent is not null) return _parent.Resolve(path);
            throw new DirectoryNotFoundException($"No VFS mount found for path: {path}");
        }

        // Build the resolved VfsPath.
        // Non-aliased fast path: original VfsPath is already the resolved path - zero alloc.
        // Aliased: stream/query spans from the original path must be grafted onto the expanded base.
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
                // Rare: alias + stream/query - assemble full path in a stack buffer.
                var baseSpan = effective.PathSpan;
                Span<char> buf = stackalloc char[VfsPath.MaxLength];
                baseSpan.CopyTo(buf);
                int len = baseSpan.Length;
                if (!streamSpan.IsEmpty) { buf[len++] = ':'; streamSpan.CopyTo(buf[len..]); len += streamSpan.Length; }
                if (!querySpan.IsEmpty)  { buf[len++] = '?'; querySpan.CopyTo(buf[len..]); len += querySpan.Length; }
                resolvedPath = VfsPath.From(buf[..len], path.IsCaseSensitive);
            }
        }

        return (matchNode, matchKey, resolvedPath);
    }

    // -- Alias expansion -------------------------------------------------------

    // Returns null when no alias matched (zero allocation - the common case).
    // Receives the alias snapshot captured at the start of Resolve so a concurrent
    // Alias() call mid-resolution sees a consistent view.
    private static VfsPath? ExpandAliases(
        VfsPath path,
        (VfsPath Alias, VfsPath Target)[] aliases,
        int depth)
    {
        if (depth > 20)
            throw new InvalidOperationException(
                $"VFS alias depth limit exceeded at depth {depth}: {path}");

        foreach (var (alias, target) in aliases)
        {
            if (!path.StartsWith(alias)) continue;

            var expanded = VfsPath.Rebase(path, alias, target);
            return ExpandAliases(expanded, aliases, depth + 1) ?? expanded;
        }

        return null;
    }
}
