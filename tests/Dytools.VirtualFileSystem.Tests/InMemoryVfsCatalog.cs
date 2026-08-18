using System.Runtime.CompilerServices;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Tests;

// In-memory IVfsCatalog for tests. Not shipped in core - production catalogs are durable
// (JsonFileVfsCatalog, or a database-backed IVfsCatalog).
internal sealed class InMemoryVfsCatalog : IVfsCatalog
{
    private readonly object                            _lock    = new();
    private readonly Dictionary<string, CatalogEntry>  _entries = new(StringComparer.Ordinal);

    public InMemoryVfsCatalog()
        => _entries[""] = new CatalogEntry { Path = VfsPath.From(""), IsDirectory = true };   // root

    public ValueTask<CatalogEntry?> GetAsync(VfsPath path, CancellationToken ct = default)
    {
        lock (_lock) return new ValueTask<CatalogEntry?>(_entries.GetValueOrDefault(Key(path)));
    }

    public async IAsyncEnumerable<CatalogEntry> ListChildrenAsync(
        VfsPath path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = Key(path);
        List<CatalogEntry> children;
        lock (_lock)
            children = _entries.Where(kv => kv.Key.Length > 0 && Parent(kv.Key) == key)
                               .Select(kv => kv.Value).ToList();

        foreach (var c in children) { ct.ThrowIfCancellationRequested(); yield return c; }
        await Task.CompletedTask;
    }

    public ValueTask<int> ReferenceCountAsync(string contentId, CancellationToken ct = default)
    {
        lock (_lock) return new ValueTask<int>(_entries.Values.Count(e => e.ContentId == contentId));
    }

    public ValueTask<string?> FindContentIdByHashAsync(string hash, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (var e in _entries.Values)
                if (e.Hash == hash && e.ContentId is not null)
                    return new ValueTask<string?>(e.ContentId);
            return new ValueTask<string?>((string?)null);
        }
    }

    public ValueTask<CatalogEntry?> PutFileAsync(CatalogEntry file, CancellationToken ct = default)
    {
        var key = Key(file.Path);
        lock (_lock)
        {
            EnsureDir(Parent(key), file.ModifiedAt);
            _entries.TryGetValue(key, out var prev);
            if (prev is { IsDirectory: true })
                throw new IOException($"Cannot write file over existing directory: '{key}'.");
            _entries[key] = file with { IsDirectory = false };
            return new ValueTask<CatalogEntry?>(prev);
        }
    }

    public ValueTask EnsureDirectoryAsync(VfsPath path, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        lock (_lock) EnsureDir(Key(path), timestamp);
        return default;
    }

    public async IAsyncEnumerable<CatalogEntry> RemoveAsync(
        VfsPath path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = Key(path);
        List<CatalogEntry> removedFiles = new();
        lock (_lock)
        {
            if (_entries.ContainsKey(key))
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
            }
        }
        foreach (var f in removedFiles) { ct.ThrowIfCancellationRequested(); yield return f; }
        await Task.CompletedTask;
    }

    public ValueTask MoveAsync(VfsPath fromPath, VfsPath toPath, CancellationToken ct = default)
    {
        var from = Key(fromPath);
        var to   = Key(toPath);
        lock (_lock)
        {
            if (from.Length == 0) throw new InvalidOperationException("Cannot move the catalog root.");
            if (!_entries.ContainsKey(from)) throw new FileNotFoundException($"No catalog entry at '{from}'.");

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
        }
        return default;
    }

    public ValueTask TouchAccessedAsync(VfsPath path, DateTimeOffset accessedAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var key = Key(path);
            if (_entries.TryGetValue(key, out var e) && !e.IsDirectory)
                _entries[key] = e with { AccessedAt = accessedAt };   // immediate; no coalescing in the test double
        }
        return default;
    }

    private void EnsureDir(string key, DateTimeOffset ts)
    {
        if (key.Length == 0) return;
        var cur = "";
        foreach (var seg in key.Split('/'))
        {
            cur = cur.Length == 0 ? seg : cur + "/" + seg;
            if (!_entries.TryGetValue(cur, out var existing))
                _entries[cur] = new CatalogEntry { Path = VfsPath.From(cur), IsDirectory = true, CreatedAt = ts, ModifiedAt = ts };
            else if (!existing.IsDirectory)
                throw new IOException($"Path segment '{cur}' already exists as a file, not a directory.");
        }
    }

    private static string Key(VfsPath p) { var s = p.ToString(); return s.Length == 0 ? "" : s.Trim('/'); }
    private static string Parent(string key) { var i = key.LastIndexOf('/'); return i < 0 ? "" : key[..i]; }
}
