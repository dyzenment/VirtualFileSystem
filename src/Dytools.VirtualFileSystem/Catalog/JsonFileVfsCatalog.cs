using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dytools.VirtualFileSystem.Catalog;

// Durable IVfsCatalog that persists the whole namespace as a single JSON document in a
// backing store (any IVfsNode) - so a dedupe mount works with no external database. Loads
// once, keeps the namespace in memory, and rewrites the document on every change
// (atomically: write a temp file, then rename over the target).
//
// Register it to use it; there is no implicit default. Give it a store at registration:
//   services.AddVfsJsonCatalog(sp => sp.NodeAt("/disk/catalog"));
//
// Great for getting started and small/medium namespaces. Each save is O(entries); for
// high write volume implement IVfsCatalog over a database instead.
//
// Partition-capable: ForPartition(key) returns a catalog backed by a separate file in the
// same store, so one registration can serve several mounts with isolated namespaces and
// reference counts.
public sealed class JsonFileVfsCatalog : IPartitionedVfsCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
        Converters             = { new VfsPathJsonConverter() },
    };

    // Access-time updates are coalesced to this granularity: a read only rewrites the document when
    // the last recorded access is older than this. Bounds write amplification (otherwise every read
    // would rewrite the whole file); AccessedAt is best-effort, so coarse resolution is fine.
    private static readonly TimeSpan AccessResolution = TimeSpan.FromMinutes(1);

    private readonly IVfsNode      _store;
    private readonly string        _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, CatalogEntry>? _entries;   // keyed by canonical path string; null until loaded

    public JsonFileVfsCatalog(IVfsNode store, string path = ".vfs-catalog.json")
    {
        _store = store;
        _path  = path;
    }

    public IVfsCatalog ForPartition(string partitionKey)
        => new JsonFileVfsCatalog(_store, PartitionFile(_path, partitionKey));

    // -- Reads -----------------------------------------------------------------

    public async ValueTask<CatalogEntry?> GetAsync(VfsPath path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureLoadedAsync(ct); return _entries!.GetValueOrDefault(Key(path)); }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<CatalogEntry> ListChildrenAsync(
        VfsPath path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<CatalogEntry> children;
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = Key(path);
            children = _entries!.Where(kv => kv.Key.Length > 0 && Parent(kv.Key) == key)
                                .Select(kv => kv.Value).ToList();
        }
        finally { _gate.Release(); }

        foreach (var c in children) { ct.ThrowIfCancellationRequested(); yield return c; }
    }

    public async ValueTask<int> ReferenceCountAsync(string contentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureLoadedAsync(ct); return _entries!.Values.Count(e => e.ContentId == contentId); }
        finally { _gate.Release(); }
    }

    public async ValueTask<string?> FindContentIdByHashAsync(string hash, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            foreach (var e in _entries!.Values)
                if (e.Hash == hash && e.ContentId is not null) return e.ContentId;
            return null;
        }
        finally { _gate.Release(); }
    }

    // -- Mutations -------------------------------------------------------------

    public async ValueTask<CatalogEntry?> PutFileAsync(CatalogEntry file, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = Key(file.Path);
            EnsureDir(Parent(key), file.ModifiedAt);
            _entries!.TryGetValue(key, out var prev);
            if (prev is { IsDirectory: true })
                throw new IOException($"Cannot write file over existing directory: '{key}'.");
            _entries[key] = file with { IsDirectory = false };
            await SaveAsync(ct);
            return prev;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask EnsureDirectoryAsync(VfsPath path, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureLoadedAsync(ct); EnsureDir(Key(path), timestamp); await SaveAsync(ct); }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<CatalogEntry> RemoveAsync(
        VfsPath path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<CatalogEntry> removedFiles = new();
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = Key(path);
            if (_entries!.ContainsKey(key))
            {
                var prefix = key + "/";
                foreach (var k in _entries.Keys
                             .Where(k => k == key || k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                {
                    if (k.Length == 0) continue;
                    var e = _entries[k];
                    _entries.Remove(k);
                    if (!e.IsDirectory) removedFiles.Add(e);
                }
                await SaveAsync(ct);
            }
        }
        finally { _gate.Release(); }

        foreach (var f in removedFiles) { ct.ThrowIfCancellationRequested(); yield return f; }
    }

    public async ValueTask MoveAsync(VfsPath fromPath, VfsPath toPath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var from = Key(fromPath);
            var to   = Key(toPath);
            if (from.Length == 0) throw new InvalidOperationException("Cannot move the catalog root.");
            if (!_entries!.ContainsKey(from)) throw new FileNotFoundException($"No catalog entry at '{from}'.");

            var toPrefix = to + "/";
            foreach (var k in _entries.Keys
                         .Where(k => k == to || k.StartsWith(toPrefix, StringComparison.Ordinal)).ToList())
                _entries.Remove(k);

            EnsureDir(Parent(to), DateTimeOffset.UtcNow);

            var fromPrefix = from + "/";
            foreach (var k in _entries.Keys
                         .Where(k => k == from || k.StartsWith(fromPrefix, StringComparison.Ordinal)).ToList())
            {
                var e = _entries[k];
                _entries.Remove(k);
                var newKey = k.Length == from.Length ? to : to + k[from.Length..];
                _entries[newKey] = e with { Path = VfsPath.From(newKey) };
            }
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask TouchAccessedAsync(VfsPath path, DateTimeOffset accessedAt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!_entries!.TryGetValue(Key(path), out var e) || e.IsDirectory) return;
            if (e.AccessedAt is { } prev && accessedAt - prev < AccessResolution) return;   // coalesce
            _entries[Key(path)] = e with { AccessedAt = accessedAt };
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }
    }

    // -- Persistence (call under _gate) ----------------------------------------

    private async ValueTask EnsureLoadedAsync(CancellationToken ct)
    {
        if (_entries is not null) return;

        var entries = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal)
        {
            [""] = new CatalogEntry { Path = VfsPath.From(""), IsDirectory = true },   // root
        };

        var stream = await _store.OpenReadAsync(new VfsNodeRequest(VfsPath.From(_path)), ct);
        if (stream is not null)
        {
            await using (stream)
            {
                var list = await JsonSerializer.DeserializeAsync<List<CatalogEntry>>(stream, JsonOpts, ct);
                if (list is not null)
                    foreach (var e in list) entries[Key(e.Path)] = e;
            }
        }
        _entries = entries;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        // Root is implicit; persist everything else.
        var payload = _entries!.Where(kv => kv.Key.Length > 0).Select(kv => kv.Value).ToList();
        var bytes   = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

        // Atomic replace: write a temp sibling, then rename over the target.
        var tmp  = _path + ".tmp";
        var leaf = _path.Contains('/') ? _path[(_path.LastIndexOf('/') + 1)..] : _path;

        await using (var w = await _store.OpenWriteAsync(new VfsNodeRequest(VfsPath.From(tmp)), VfsWriteMode.Create, ct))
            await w.WriteAsync(bytes, ct);

        await _store.RenameAsync(new VfsNodeRequest(VfsPath.From(tmp)), leaf, ct);
    }

    // -- In-memory index helpers (call under _gate) ----------------------------

    private void EnsureDir(string key, DateTimeOffset ts)
    {
        if (key.Length == 0) return;   // root always exists
        var cur = "";
        foreach (var seg in key.Split('/'))
        {
            cur = cur.Length == 0 ? seg : cur + "/" + seg;
            if (!_entries!.TryGetValue(cur, out var existing))
                _entries[cur] = new CatalogEntry { Path = VfsPath.From(cur), IsDirectory = true, CreatedAt = ts, ModifiedAt = ts };
            else if (!existing.IsDirectory)
                throw new IOException($"Path segment '{cur}' already exists as a file, not a directory.");
        }
    }

    // Canonical path string (base + stream + query), no leading/trailing '/'; "" is root.
    private static string Key(VfsPath p)
    {
        var s = p.ToString();
        return s.Length == 0 ? "" : s.Trim('/');
    }

    private static string Parent(string key) { var i = key.LastIndexOf('/'); return i < 0 ? "" : key[..i]; }

    // Insert the partition key before the file extension: ".vfs-catalog.json" + "archive"
    // → ".vfs-catalog.archive.json".
    private static string PartitionFile(string path, string partitionKey)
    {
        var safe = new string(partitionKey.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        var dot  = path.LastIndexOf('.');
        return dot <= 0 ? $"{path}.{safe}" : $"{path[..dot]}.{safe}{path[dot..]}";
    }
}
