using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Middleware;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Sample;

// Demonstrates core VFS features against in-memory nodes:
//   1. Basic read / write / delete / list
//   2. Path aliases (IsAliased)
//   3. Node-level symlinks with SymlinkMiddleware (IsSymlink)
//   4. Hard-link deduplication via IDeduplicatingNode capability
internal static class BasicDemo
{
    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        var dedup    = new DeduplicatingInMemoryNode();

        services
            .AddVirtualFileSystem()
            // UseSymlinks(): only checks nodes that implement ISymlinkCapableNode (zero overhead for others).
            // UseSymlinks(typeof(SomeThirdPartyNode)): also check specific types you can't modify.
            .UseSymlinks()
            .Mount("/mem",   new SymlinkAwareInMemoryNode())  // implements ISymlinkCapableNode → checked
            .Mount("/dedup", dedup)                           // does not → skipped by SymlinkMiddleware
            .Alias("/docs",  "/mem/documents");               // /docs → /mem/documents

        var provider = services.BuildServiceProvider();
        provider.InitializeVirtualFileSystem();

        await using var vfs = provider.GetRequiredService<IVirtualFileSystem>();

        // ── Demo 1 - Basic read / write / delete ──────────────────────────────
        Section("1 · Basic read / write / delete");

        await WriteText(vfs, "/mem/hello.txt", "Hello, VFS!");
        Pass($"Exists before delete: {await vfs.ExistsAsync("/mem/hello.txt")}");
        await vfs.DeleteAsync("/mem/hello.txt");
        Pass($"Exists after delete:  {await vfs.ExistsAsync("/mem/hello.txt")}");

        // Typed sugar: JSON over stream
        await vfs.SendAsync("/mem/config.json", new { Host = "localhost", Port = 5432 });
        var cfg = await vfs.RetrieveAsync<dynamic>("/mem/config.json");
        Pass($"SendAsync / RetrieveAsync: Host = {cfg?.GetProperty("Host")}");

        // ── Demo 2 - Path aliases ─────────────────────────────────────────────
        Section("2 · Path aliases  (/docs → /mem/documents)");

        // Write to the real path, read via alias
        await WriteText(vfs, "/mem/documents/readme.txt", "Readme content");

        var aliasRead = await ReadText(vfs, "/docs/readme.txt");
        Pass($"Read via alias:   '{aliasRead}'");

        var directInfo = await vfs.GetInfoAsync("/mem/documents/readme.txt");
        var aliasInfo  = await vfs.GetInfoAsync("/docs/readme.txt");

        Pass($"Direct → IsAliased: {directInfo?.IsAliased}  (expected: False)");
        Pass($"Alias  → IsAliased: {aliasInfo?.IsAliased}   (expected: True)");
        Pass($"Direct → IsSymlink: {directInfo?.IsSymlink}  (expected: False)");
        Pass($"Alias  → IsSymlink: {aliasInfo?.IsSymlink}   (expected: False)");

        // ── Demo 3 - Node-level symlinks ──────────────────────────────────────
        Section("3 · Node-level symlinks  (SymlinkMiddleware)");

        // Write actual content at the real path
        await WriteText(vfs, "/mem/assets/logo.png", "<binary: logo bytes>");

        // Write a symlink record at a short alias path.
        // SymlinkAwareInMemoryNode treats files ending in ".lnk" as symlinks:
        // it stores the target path as the file content and surfaces SymlinkTarget
        // in Properties so SymlinkMiddleware can follow it.
        await WriteText(vfs, "/mem/logo.png.lnk", "/mem/assets/logo.png");

        // Reading the .lnk path: SymlinkMiddleware peeks at GetInfo, sees SymlinkTarget,
        // reroutes ctx, then the pipeline reads from /mem/assets/logo.png instead.
        var symlinkRead = await ReadText(vfs, "/mem/logo.png.lnk");
        Pass($"Read via symlink: '{symlinkRead}'");

        var symlinkInfo = await vfs.GetInfoAsync("/mem/logo.png.lnk");
        Pass($"Symlink → IsSymlink: {symlinkInfo?.IsSymlink}  (expected: True)");
        Pass($"Symlink → IsAliased: {symlinkInfo?.IsAliased}  (expected: False)");

        // Deleting the .lnk path removes the pointer file, NOT the target.
        // (SymlinkMiddleware does not follow on Delete - Unix semantics.)
        await vfs.DeleteAsync("/mem/logo.png.lnk");
        Pass($"Target still exists after pointer deleted: {await vfs.ExistsAsync("/mem/assets/logo.png")}");

        // ── Demo 4 - Hard-link deduplication (IDeduplicatingNode) ──────────────
        Section("4 · Hard-link deduplication  (IDeduplicatingNode)");

        const string logo = "<binary: company logo bytes>";

        // Write the same content under three different paths
        await WriteText(vfs, "/dedup/emails/1234/logo.png", logo);
        await WriteText(vfs, "/dedup/emails/5678/logo.png", logo);
        await WriteText(vfs, "/dedup/emails/9012/logo.png", logo);

        // Query the capability - available because DeduplicatingInMemoryNode implements it
        var cap  = vfs.GetCapability<IDeduplicatingNode>("/dedup/emails/1234/logo.png");
        var id   = await cap!.HardLinks.ResolveContentIdAsync("emails/1234/logo.png");
        var refs = await cap.HardLinks.GetRefCountAsync(id!);
        Pass($"Content ID (SHA-256 prefix): {id![..16]}...");
        Pass($"Refcount after 3 writes:     {refs}  (expected: 3)");

        Console.WriteLine("  Links sharing this content:");
        await foreach (var link in cap.HardLinks.GetLinksAsync(id!))
            Console.WriteLine($"    {link}");

        // Verify data is deduplicated: only one blob stored
        Pass($"Distinct blobs stored: {dedup.BlobCount}  (expected: 1)");

        // Delete one reference - blob survives
        await vfs.DeleteAsync("/dedup/emails/1234/logo.png");
        refs = await cap.HardLinks.GetRefCountAsync(id!);
        Pass($"Refcount after 1 delete:     {refs}  (expected: 2)");
        Pass($"Blobs after 1 delete:        {dedup.BlobCount}  (expected: 1)");

        // Delete remaining two - blob is freed
        await vfs.DeleteAsync("/dedup/emails/5678/logo.png");
        await vfs.DeleteAsync("/dedup/emails/9012/logo.png");
        refs = await cap.HardLinks.GetRefCountAsync(id!);
        Pass($"Refcount after all deleted:  {refs}  (expected: 0)");
        Pass($"Blobs after all deleted:     {dedup.BlobCount}  (expected: 0)");

        // InMemoryKvNode does NOT implement IDeduplicatingNode - returns null
        var noDedup = vfs.GetCapability<IDeduplicatingNode>("/mem/documents/readme.txt");
        Pass($"InMemoryKvNode IDeduplicatingNode: {noDedup?.ToString() ?? "null (as expected)"}");
    }

    // -- Helpers ---------------------------------------------------------------

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {title} --");
    }

    private static void Pass(string msg) => Console.WriteLine($"  ✓ {msg}");

    private static async Task WriteText(IVirtualFileSystem vfs, string path, string text)
    {
        await using var stream = await vfs.OpenWriteAsync(path);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    private static async Task<string> ReadText(IVirtualFileSystem vfs, string path)
    {
        await using var stream = await vfs.OpenReadAsync(path);
        if (stream is null) return "(not found)";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}

// -----------------------------------------------------------------------------
// SymlinkAwareInMemoryNode
// Composes InMemoryKvNode and surfaces VfsPropertyKeys.SymlinkTarget for files
// whose relative path ends in ".lnk". The file content is the target VFS path.
// This is a simple convention - a real implementation might use a header byte,
// a separate index, or a dedicated file extension.
// -----------------------------------------------------------------------------

// ISymlinkCapableNode tells SymlinkMiddleware to call GetInfoAsync on this node.
// Nodes that do NOT implement this are skipped with zero overhead.
internal sealed class SymlinkAwareInMemoryNode : VfsNodeBase, ISymlinkCapableNode
{
    private readonly InMemoryKvNode _inner = new();

    public override Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
        => _inner.OpenReadAsync(req, ct);

    public override Task<Stream> OpenWriteAsync(VfsNodeRequest req, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
        => _inner.OpenWriteAsync(req, mode, ct);

    public override Task DeleteAsync(VfsNodeRequest req, CancellationToken ct = default)
        => _inner.DeleteAsync(req, ct);

    public override IAsyncEnumerable<VfsNodeInfo> ListAsync(VfsNodeRequest req, CancellationToken ct = default)
        => _inner.ListAsync(req, ct);

    public override async Task<VfsNodeInfo?> GetInfoAsync(
        VfsNodeRequest request, CancellationToken ct = default)
    {
        var info = await _inner.GetInfoAsync(request, ct);
        if (info is null || !request.Path.PathSpan.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return info;

        // Read the target path from the file content
        await using var stream = await _inner.OpenReadAsync(request, ct);
        if (stream is null) return info;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var target = await reader.ReadToEndAsync(ct);

        return info with
        {
            Properties = ImmutableDictionary<string, object>.Empty
                .Add(VfsPropertyKeys.SymlinkTarget, target.Trim())
        };
    }
}

// -----------------------------------------------------------------------------
// DeduplicatingInMemoryNode
// Reference implementation of IDeduplicatingNode.
// Content-addresses writes by SHA-256. Multiple paths can reference the same
// blob; the blob is freed when the last reference is released on delete.
// -----------------------------------------------------------------------------
internal sealed class DeduplicatingInMemoryNode : VfsNodeBase, IDeduplicatingNode
{
    private readonly Dictionary<string, byte[]> _blobs = [];   // contentId → bytes
    private readonly Dictionary<string, string> _index = new(StringComparer.OrdinalIgnoreCase);  // path → contentId
    private readonly Dictionary<string, int>    _refs  = [];   // contentId → refcount

    private readonly SimpleHardLinkStore _store;

    public DeduplicatingInMemoryNode()
        => _store = new SimpleHardLinkStore(_blobs, _index, _refs);

    public IHardLinkStore HardLinks => _store;

    // Exposed for demo assertions - not part of IDeduplicatingNode
    public int BlobCount => _blobs.Count;

    public override Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var relPath = new string(req.Path.PathSpan);
        if (!_index.TryGetValue(relPath, out var id)) return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(new MemoryStream(_blobs[id], writable: false));
    }

    public override Task<Stream> OpenWriteAsync(
        VfsNodeRequest req, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
    {
        var relPath = new string(req.Path.PathSpan);
        if (mode == VfsWriteMode.CreateNew && _index.ContainsKey(relPath))
            throw new IOException($"Key already exists: {relPath}");

        // Release old reference if overwriting
        if (_index.TryGetValue(relPath, out var oldId))
            Release(oldId, relPath);

        return Task.FromResult<Stream>(new CommitStream(data =>
        {
            var id = Sha256(data);
            if (!_blobs.ContainsKey(id)) _blobs[id] = data;
            _index[relPath] = id;
            _refs[id] = _refs.TryGetValue(id, out var c) ? c + 1 : 1;
        }));
    }

    public override Task DeleteAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var relPath = new string(req.Path.PathSpan);
        if (_index.TryGetValue(relPath, out var id))
            Release(id, relPath);
        return Task.CompletedTask;
    }

    public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var relPath = new string(req.Path.PathSpan);
        if (!_index.TryGetValue(relPath, out var id)) return Task.FromResult<VfsNodeInfo?>(null);
        return Task.FromResult<VfsNodeInfo?>(new VfsNodeInfo
        {
            RelativePath = req.Path,
            IsFile       = true,
            IsDirectory  = false,
            SizeBytes    = _blobs[id].Length,
            Properties   = ImmutableDictionary<string, object>.Empty
                               .Add(VfsPropertyKeys.ContentId, id),
        });
    }

    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prefix = new string(req.Path.PathSpan).TrimEnd('/') + "/";
        foreach (var (path, id) in _index)
        {
            ct.ThrowIfCancellationRequested();
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var remainder = path[prefix.Length..];
            if (remainder.Contains('/')) continue;
            yield return new VfsNodeInfo
            {
                RelativePath = VfsPath.From(path),
                IsFile       = true,
                IsDirectory  = false,
                SizeBytes    = _blobs[id].Length,
            };
        }
    }

    private void Release(string contentId, string path)
    {
        _index.Remove(path);
        if (!_refs.TryGetValue(contentId, out var c)) return;
        var next = c - 1;
        if (next <= 0) { _refs.Remove(contentId); _blobs.Remove(contentId); }
        else           { _refs[contentId] = next; }
    }

    private static string Sha256(byte[] data)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
}

// IHardLinkStore backed by the same dictionaries as DeduplicatingInMemoryNode
internal sealed class SimpleHardLinkStore(
    Dictionary<string, byte[]> blobs,
    Dictionary<string, string> index,
    Dictionary<string, int>    refs) : IHardLinkStore
{
    public ValueTask<string?> ResolveContentIdAsync(string relativePath, CancellationToken ct = default)
        => ValueTask.FromResult(index.TryGetValue(relativePath, out var id) ? id : null);

    public ValueTask<int> AddReferenceAsync(string contentId, string relativePath, CancellationToken ct = default)
    {
        index[relativePath] = contentId;
        refs[contentId] = refs.TryGetValue(contentId, out var c) ? c + 1 : 1;
        return ValueTask.FromResult(refs[contentId]);
    }

    public ValueTask<int> ReleaseReferenceAsync(string relativePath, CancellationToken ct = default)
    {
        if (!index.Remove(relativePath, out var id)) return ValueTask.FromResult(0);
        if (!refs.TryGetValue(id, out var c)) return ValueTask.FromResult(0);
        var next = c - 1;
        if (next <= 0) { refs.Remove(id); blobs.Remove(id); return ValueTask.FromResult(0); }
        refs[id] = next;
        return ValueTask.FromResult(next);
    }

    public ValueTask<int> GetRefCountAsync(string contentId, CancellationToken ct = default)
        => ValueTask.FromResult(refs.TryGetValue(contentId, out var c) ? c : 0);

    public async IAsyncEnumerable<string> GetLinksAsync(
        string contentId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (path, id) in index)
        {
            ct.ThrowIfCancellationRequested();
            if (id == contentId) yield return path;
        }
        await Task.CompletedTask;
    }
}

// Write stream that commits to a callback on dispose
internal sealed class CommitStream(Action<byte[]> commit) : MemoryStream
{
    private bool _committed;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_committed) { _committed = true; commit(ToArray()); }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_committed) { _committed = true; commit(ToArray()); }
        await base.DisposeAsync();
    }
}
