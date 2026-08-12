using System.Runtime.CompilerServices;

namespace Dytools.VirtualFileSystem.Catalog;

// Default in-memory IVfsCatalog. Thread-safe via a single lock. Intended for tests
// and non-durable use - for production, implement IVfsCatalog over your database
// (the namespace is the source of truth and must outlive the process).
public sealed class InMemoryVfsCatalog : IVfsCatalog
{
    private readonly object                            _lock    = new();
    private readonly Dictionary<string, CatalogEntry>  _entries = new(StringComparer.Ordinal);

    public InMemoryVfsCatalog()
    {
        // Root always exists as a directory.
        _entries[""] = new CatalogEntry { Path = "", IsDirectory = true };
    }

    public ValueTask<CatalogEntry?> GetAsync(string path, CancellationToken ct = default)
    {
        var key = Norm(path);
        lock (_lock)
            return new ValueTask<CatalogEntry?>(_entries.TryGetValue(key, out var e) ? e : null);
    }

    public async IAsyncEnumerable<CatalogEntry> ListChildrenAsync(
        string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = Norm(path);
        List<CatalogEntry> children;
        lock (_lock)
            children = _entries.Values.Where(e => e.Path.Length > 0 && Parent(e.Path) == key).ToList();

        foreach (var c in children)
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
        }
        await Task.CompletedTask;
    }

    public ValueTask<int> ReferenceCountAsync(string contentId, CancellationToken ct = default)
    {
        lock (_lock)
            return new ValueTask<int>(_entries.Values.Count(e => e.ContentId == contentId));
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
        var key = Norm(file.Path);
        lock (_lock)
        {
            EnsureDirLocked(Parent(key), file.ModifiedAt);
            _entries.TryGetValue(key, out var prev);
            if (prev is { IsDirectory: true })
                throw new IOException($"Cannot write file over existing directory: '{key}'.");
            _entries[key] = file with { Path = key, IsDirectory = false };
            return new ValueTask<CatalogEntry?>(prev);
        }
    }

    public ValueTask EnsureDirectoryAsync(string path, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        lock (_lock) EnsureDirLocked(Norm(path), timestamp);
        return default;
    }

    public async IAsyncEnumerable<CatalogEntry> RemoveAsync(
        string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = Norm(path);
        List<CatalogEntry> removedFiles = new();
        lock (_lock)
        {
            if (_entries.ContainsKey(key))
            {
                var prefix = key + "/";
                var toRemove = _entries.Keys
                    .Where(k => k == key || k.StartsWith(prefix, StringComparison.Ordinal))
                    .ToList();
                foreach (var k in toRemove)
                {
                    if (k.Length == 0) continue;            // never remove the root itself
                    var e = _entries[k];
                    _entries.Remove(k);
                    if (!e.IsDirectory) removedFiles.Add(e);
                }
            }
        }
        foreach (var f in removedFiles)
        {
            ct.ThrowIfCancellationRequested();
            yield return f;
        }
        await Task.CompletedTask;
    }

    public ValueTask MoveAsync(string fromPath, string toPath, CancellationToken ct = default)
    {
        var from = Norm(fromPath);
        var to   = Norm(toPath);
        lock (_lock)
        {
            if (from.Length == 0) throw new InvalidOperationException("Cannot move the catalog root.");
            if (!_entries.ContainsKey(from))
                throw new FileNotFoundException($"No catalog entry at '{from}'.");

            // Drop any existing target subtree so the move replaces it.
            var toPrefix = to + "/";
            foreach (var k in _entries.Keys
                         .Where(k => k == to || k.StartsWith(toPrefix, StringComparison.Ordinal)).ToList())
                _entries.Remove(k);

            EnsureDirLocked(Parent(to), DateTimeOffset.UtcNow);

            // Re-key the from-subtree.
            var fromPrefix = from + "/";
            var moves = _entries.Keys
                .Where(k => k == from || k.StartsWith(fromPrefix, StringComparison.Ordinal)).ToList();
            foreach (var k in moves)
            {
                var e = _entries[k];
                _entries.Remove(k);
                var newKey = k.Length == from.Length ? to : to + k[from.Length..];
                _entries[newKey] = e with { Path = newKey };
            }
        }
        return default;
    }

    // -- helpers (call under _lock) --------------------------------------------

    private void EnsureDirLocked(string path, DateTimeOffset ts)
    {
        if (path.Length == 0) return;   // root always exists
        var cur = "";
        foreach (var seg in path.Split('/'))
        {
            cur = cur.Length == 0 ? seg : cur + "/" + seg;
            if (!_entries.TryGetValue(cur, out var existing))
                _entries[cur] = new CatalogEntry { Path = cur, IsDirectory = true, CreatedAt = ts, ModifiedAt = ts };
            else if (!existing.IsDirectory)
                throw new IOException($"Path segment '{cur}' already exists as a file, not a directory.");
        }
    }

    private static string Norm(string p) => p.Trim('/');
    private static string Parent(string p) { var i = p.LastIndexOf('/'); return i < 0 ? "" : p[..i]; }
}
