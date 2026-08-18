using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Dytools.VirtualFileSystem.Catalog;

// Reusable catalog-backed namespace mirror for nodes that want fast local listings over a slow or
// costly backend. It holds the catalog, serves listings from it, writes mutations through, and
// keeps small sync state (a delta cursor, a "seeded" marker) in one reserved catalog entry that is
// hidden from listings. The node decides HOW to refresh - a full re-list, or an incremental delta -
// and drives WHEN (every list, or once + on demand); this type is the shared plumbing underneath.
//
// Concurrency: SyncAsync serializes refreshes; serving and write-through are catalog operations,
// which implementations already make safe for concurrent use.
public sealed class CatalogMirror
{
    // One reserved entry holds all mirror state (keyed values in its Properties). Hidden from lists.
    private static readonly VfsPath StateEntry = VfsPath.From(".vfs-mirror-state");

    private readonly IVfsCatalog   _catalog;
    private readonly SemaphoreSlim  _gate = new(1, 1);

    public CatalogMirror(IVfsCatalog catalog) => _catalog = catalog;

    public IVfsCatalog Catalog => _catalog;

    // -- Serve -----------------------------------------------------------------

    // Immediate children from the mirror, with the reserved state entry filtered out.
    public async IAsyncEnumerable<CatalogEntry> ListChildrenAsync(
        VfsPath path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in _catalog.ListChildrenAsync(path, ct))
            if (e.Path != StateEntry)
                yield return e;
    }

    // -- Refresh (serialized) --------------------------------------------------

    // Runs the node's refresh under the mirror's lock, so concurrent listings don't double-sync.
    public async Task SyncAsync(Func<CancellationToken, Task> refresh, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await refresh(ct); }
        finally { _gate.Release(); }
    }

    // -- Sync state (cursor / seeded marker) in the reserved entry -------------

    public async Task<string?> GetStateAsync(string key, CancellationToken ct = default)
        => (await _catalog.GetAsync(StateEntry, ct))?.Properties.GetString(key);

    public async Task SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        var current = (await _catalog.GetAsync(StateEntry, ct))?.Properties;
        var props   = current is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(current);
        props[key]  = value;
        await _catalog.PutFileAsync(new CatalogEntry { Path = StateEntry, IsDirectory = false, Properties = props }, ct);
    }

    // -- Write-through ---------------------------------------------------------

    public async Task UpsertAsync(VfsNodeInfo info, CancellationToken ct = default)
    {
        if (info.IsDirectory) await _catalog.EnsureDirectoryAsync(info.RelativePath, info.ModifiedAt ?? default, ct);
        else                  await _catalog.PutFileAsync(ToEntry(info), ct);
    }

    public async Task RemoveAsync(VfsPath path, CancellationToken ct = default)
    {
        await foreach (var _ in _catalog.RemoveAsync(path, ct)) { }   // drain the removed-file stream
    }

    public async Task MoveAsync(VfsPath from, VfsPath to, CancellationToken ct = default)
    {
        if (await _catalog.GetAsync(from, ct) is not null)            // only if the source is mirrored
            await _catalog.MoveAsync(from, to, ct);
    }

    // Best-effort access-time supplement for backends that don't track it: record a read against the
    // mirror. No-ops for a path that isn't mirrored yet, and the catalog coalesces the write.
    public Task TouchAccessedAsync(VfsPath path, DateTimeOffset accessedAt, CancellationToken ct = default)
        => _catalog.TouchAccessedAsync(path, accessedAt, ct).AsTask();

    // Wipe every mirrored entry (keeping the reserved state) - used before a full re-list.
    public async Task ClearAsync(CancellationToken ct = default)
    {
        var roots = new List<VfsPath>();
        await foreach (var e in _catalog.ListChildrenAsync(VfsPath.From(""), ct))
            if (e.Path != StateEntry)
                roots.Add(e.Path);
        foreach (var p in roots)
            await RemoveAsync(p, ct);
    }

    // -- Conversions -----------------------------------------------------------

    public static VfsNodeInfo ToNodeInfo(CatalogEntry e) => new()
    {
        RelativePath = e.Path,
        IsFile       = !e.IsDirectory,
        IsDirectory  = e.IsDirectory,
        IsHidden     = e.IsHidden,
        SizeBytes    = e.Size,
        CreatedAt    = e.CreatedAt,
        ModifiedAt   = e.ModifiedAt,
        AccessedAt   = e.AccessedAt,
        Properties   = e.Properties is null
            ? ImmutableDictionary<string, string?>.Empty
            : ImmutableDictionary.CreateRange(e.Properties),
    };

    private static CatalogEntry ToEntry(VfsNodeInfo info) => new()
    {
        Path        = info.RelativePath,
        IsDirectory = info.IsDirectory,
        Size        = info.SizeBytes,
        CreatedAt   = info.CreatedAt  ?? default,
        ModifiedAt  = info.ModifiedAt ?? default,
        AccessedAt  = info.AccessedAt,
        ContentType = info.Properties.GetString("ContentType"),
        Properties  = info.Properties.Count > 0 ? new Dictionary<string, string?>(info.Properties) : null,
    };
}
