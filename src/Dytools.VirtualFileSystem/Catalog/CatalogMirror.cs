using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Dytools.VirtualFileSystem.Catalog;

// Reusable catalog-backed namespace mirror for nodes that want fast local listings over a slow or
// costly backend. It holds the catalog, serves listings from it, writes mutations through, and
// keeps small sync state (a delta cursor, a "seeded" marker, a sync lease) under a reserved,
// hidden state directory. The node decides HOW to refresh - a full re-list, or an incremental delta
// - and drives WHEN; this type is the shared plumbing, including the atomic state primitives a node
// uses to gate refreshes across instances (SetIfNullStateAsync + an expiring `sync-expires` lease).
//
// State keys are stored as INDEPENDENT reserved entries (one per key) rather than fields of a single
// entry, so a write to one key never rewrites another - which is what makes SetIfNullStateAsync's
// splitter correct across processes sharing one catalog. Serving and write-through are catalog
// operations, which implementations already make safe for concurrent use.
public sealed class CatalogMirror
{
    // Reserved directory holding one entry per state key. Hidden from listings.
    private const string StateDir = ".vfs-mirror-state";

    private readonly IVfsCatalog _catalog;

    public CatalogMirror(IVfsCatalog catalog) => _catalog = catalog;

    public IVfsCatalog Catalog => _catalog;

    private static VfsPath StatePath(string key) => VfsPath.From($"{StateDir}/{key}");

    private static bool IsStatePath(VfsPath p)
    {
        var s = p.ToString();
        return s == StateDir || s.StartsWith(StateDir + "/", StringComparison.Ordinal);
    }

    // -- Serve -----------------------------------------------------------------

    // Immediate children from the mirror, with the reserved state directory filtered out.
    public async IAsyncEnumerable<CatalogEntry> ListChildrenAsync(
        VfsPath path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in _catalog.ListChildrenAsync(path, ct))
            if (!IsStatePath(e.Path))
                yield return e;
    }

    // -- Sync state (cursor, seeded marker, sync lease) - one reserved entry per key ---------------

    public async Task<string?> GetStateAsync(string key, CancellationToken ct = default)
        => (await _catalog.GetAsync(StatePath(key), ct))?.Properties.GetString("v");

    public Task SetStateAsync(string key, string value, CancellationToken ct = default)
        => _catalog.PutEntryAsync(new CatalogEntry
        {
            Path       = StatePath(key),
            IsDirectory = false,
            Properties = new Dictionary<string, string?> { ["v"] = value },
        }, ct).AsTask();

    public Task ClearStateAsync(string key, CancellationToken ct = default)
        => _catalog.RemoveAsync(new[] { StatePath(key) }, ct).AsTask();

    private const int MaxAcquireLoops = 5;                                // then throw - persistent contention is abnormal
    private static readonly TimeSpan GateTtl = TimeSpan.FromSeconds(3);   // splitter gate lifetime (self-lapses, never cleared)
    internal static int BackoffMinMs = 2000, BackoffMaxMs = 7000;        // ADB backoff bounds (tunable down for tests)

    private static int BackoffMs() => BackoffMinMs + Random.Shared.Next(0, Math.Max(1, BackoffMaxMs - BackoffMinMs + 1));

    // Atomically set `key` to `value` only if it is currently absent: returns true iff this call set it,
    // false if another caller holds it, and THROWS TimeoutException if MaxAcquireLoops rounds of backoff
    // can't resolve the contention. A Moir-Anderson splitter over three independent registers - the
    // target `key`, a nonce `key.x` (X), and a short self-expiring gate `key.g` (Y) - wrapped in an
    // ADB-style retry loop: the sole splitter winner writes `key` and returns true; losers never touch
    // `key` (so the lease is never orphaned), back off a random 2-7s, and retry. The gate carries a
    // small expiry, so a slow loser that sets it late just lapses in GateTtl instead of wedging.
    // Guarantees NEVER TWO (the splitter) and NEVER ZERO in the common case (the loop). `value` is
    // opaque - expiry-based takeover of a stale `key` is the caller's business. Cancellation after we've
    // written `key` rolls that write back (only if it still holds exactly our value).
    public async Task<bool> SetIfNullStateAsync(string key, string value, CancellationToken ct = default)
    {
        var testKey = key + ".x";
        var gateKey = key + ".g";
        var nonce   = Guid.NewGuid().ToString("N");
        var wrote   = false;
        try
        {
            for (var i = 0; i < MaxAcquireLoops; i++)
            {
                if (await GetStateAsync(key, ct) is not null) return false;             // a winner already holds the key

                await SetStateAsync(testKey, nonce, ct);                                // X <- me
                if (GateActive(await GetStateAsync(gateKey, ct)))                       // someone is mid-acquire → defer
                {
                    await Task.Delay(BackoffMs(), ct);
                    continue;
                }
                await SetStateAsync(gateKey, GateValue(), ct);                          // Y <- me (self-lapsing)
                if (await GetStateAsync(testKey, ct) == nonce)                          // sole winner (splitter: at most one)
                {
                    if (await GetStateAsync(key, ct) is not null) return false;
                    await SetStateAsync(key, value, ct);                                // only the winner writes the lease
                    wrote = true;
                    await ClearStateAsync(gateKey, ct);                                 // done racing → drop the gate so re-acquire isn't blocked for GateTtl
                    ct.ThrowIfCancellationRequested();                                  // cancelled during the write → roll back
                    return true;
                }

                await Task.Delay(BackoffMs(), ct);              // lost X → back off, retry
            }
            throw new TimeoutException(
                $"Could not acquire mirror state '{key}' after {MaxAcquireLoops} attempts (persistent contention).");
        }
        catch (OperationCanceledException)
        {
            if (wrote && await GetStateAsync(key, CancellationToken.None) == value)
                await ClearStateAsync(key, CancellationToken.None);
            throw;
        }
    }

    private static string GateValue() => (DateTimeOffset.UtcNow + GateTtl).ToUnixTimeMilliseconds().ToString();

    private static bool GateActive(string? gate)
        => gate is not null && long.TryParse(gate, out var ms) && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < ms;

    // -- Write-through ---------------------------------------------------------

    // ToEntry carries IsDirectory, so PutEntryAsync ensures a dir or upserts a file as appropriate.
    public Task UpsertAsync(VfsNodeInfo info, CancellationToken ct = default)
        => _catalog.PutEntryAsync(ToEntry(info), ct).AsTask();

    // Bulk upsert - one persist for the whole set when the catalog supports it (the seeding fast path).
    // ToEntry carries IsDirectory, so PutEntriesAsync ensures dirs and upserts files as appropriate.
    public Task UpsertAsync(IEnumerable<VfsNodeInfo> infos, CancellationToken ct = default)
        => _catalog.PutEntriesAsync(infos.Select(ToEntry), ct).AsTask();

    public async Task RemoveAsync(VfsPath path, CancellationToken ct = default)
    {
        await foreach (var _ in _catalog.RemoveAsync(path, ct)) { }   // drain the removed-file stream
    }

    // Bulk remove - one persist for the whole set when the catalog supports it.
    public Task RemoveAsync(IEnumerable<VfsPath> paths, CancellationToken ct = default)
        => _catalog.RemoveAsync(paths, ct).AsTask();

    public async Task MoveAsync(VfsPath from, VfsPath to, CancellationToken ct = default)
    {
        if (await _catalog.GetAsync(from, ct) is not null)            // only if the source is mirrored
            await _catalog.MoveAsync(from, to, ct);
    }

    // Best-effort access-time supplement for backends that don't track it: record a read against the
    // mirror. No-ops for a path that isn't mirrored yet, and the catalog coalesces the write.
    public Task TouchAccessedAsync(VfsPath path, DateTimeOffset accessedAt, CancellationToken ct = default)
        => _catalog.TouchAccessedAsync(path, accessedAt, ct).AsTask();

    // Wipe every mirrored entry (keeping the reserved state directory) - used before a full re-list.
    // One bulk remove so a populated mirror isn't re-persisted per root.
    public async Task ClearAsync(CancellationToken ct = default)
    {
        var roots = new List<VfsPath>();
        await foreach (var e in _catalog.ListChildrenAsync(VfsPath.From(""), ct))
            if (!IsStatePath(e.Path))
                roots.Add(e.Path);
        await _catalog.RemoveAsync(roots, ct);
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
