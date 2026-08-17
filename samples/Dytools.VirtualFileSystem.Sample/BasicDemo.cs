using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Middleware;
using Dytools.VirtualFileSystem.Nodes.Dedupe;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Sample;

// Demonstrates core VFS features against in-memory nodes:
//   1. Basic read / write / delete / list
//   2. Path aliases (IsAliased)
//   3. Node-level symlinks with SymlinkMiddleware (IsSymlink)
//   4. Content-addressed deduplication with the real DedupeNode + catalog
internal static class BasicDemo
{
    public static async Task RunAsync()
    {
        var services = new ServiceCollection();

        services
            // The real dedupe stack: a durable JSON catalog persisted into an in-memory backing
            // store, with DedupeNode content-addressing writes over it (SHA-256, copy-on-write).
            .AddVfsJsonCatalog(sp => sp.NodeAt("/dev/store"))
            .AddVirtualFileSystem()
            // UseSymlinks(): only checks nodes that implement ISymlinkCapableNode (zero overhead for others).
            // UseSymlinks(typeof(SomeThirdPartyNode)): also check specific types you can't modify.
            .UseSymlinks()
            .Mount("/mem", new SymlinkAwareInMemoryNode())            // implements ISymlinkCapableNode → checked
            .MountSingleton<InMemoryKvNode>("/dev/store")             // physical blob store for /dedup
            .MountSingleton<DedupeNode>("/dedup", o => o.UseSource("/dev/store/blobs"))  // blobs nested under /blobs
            .Alias("/docs", "/mem/documents");                       // /docs → /mem/documents

        var provider = services.BuildServiceProvider();
        provider.InitializeVirtualFileSystem();

        await using var vfs = provider.GetRequiredService<IVirtualFileSystem>();

        // -- Demo 1 - Basic read / write / delete ------------------------------
        Section("1 · Basic read / write / delete");

        await WriteText(vfs, "/mem/hello.txt", "Hello, VFS!");
        Pass($"Exists before delete: {await vfs.ExistsAsync("/mem/hello.txt")}");
        await vfs.DeleteAsync("/mem/hello.txt");
        Pass($"Exists after delete:  {await vfs.ExistsAsync("/mem/hello.txt")}");

        // Typed sugar: JSON over stream
        await vfs.SendAsync("/mem/config.json", new { Host = "localhost", Port = 5432 });
        var cfg = await vfs.RetrieveAsync<dynamic>("/mem/config.json");
        Pass($"SendAsync / RetrieveAsync: Host = {cfg?.GetProperty("Host")}");

        // -- Demo 2 - Path aliases ---------------------------------------------
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

        // -- Demo 3 - Node-level symlinks --------------------------------------
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

        // -- Demo 4 - Content-addressed deduplication (real DedupeNode + catalog) --
        Section("4 · Content-addressed deduplication  (DedupeNode + catalog)");

        const string logo = "<binary: company logo bytes>";

        // Write the same content under three different paths.
        await WriteText(vfs, "/dedup/emails/1234/logo.png", logo);
        await WriteText(vfs, "/dedup/emails/5678/logo.png", logo);
        await WriteText(vfs, "/dedup/emails/9012/logo.png", logo);

        // The dedupe node exposes its catalog (the durable path→content map) as a capability.
        var catalog = vfs.GetCapability<IVfsCatalog>("/dedup")!;
        var id      = (await catalog.GetAsync(VfsPath.From("emails/1234/logo.png")))!.ContentId!;
        Pass($"Content ID (SHA-256): {id[..16]}...");
        Pass($"Refcount after 3 writes:      {await catalog.ReferenceCountAsync(id)}  (expected: 3)");

        // All three paths resolve to the same stored content.
        var shared = (await catalog.GetAsync(VfsPath.From("emails/5678/logo.png")))!.ContentId == id
                  && (await catalog.GetAsync(VfsPath.From("emails/9012/logo.png")))!.ContentId == id;
        Pass($"All three paths share the blob: {shared}  (expected: True)");

        // Three files reference exactly one physical blob in the backing store.
        Pass($"Blobs stored under /dev/store:  {await CountFiles(vfs, "/dev/store/blobs")}  (expected: 1)");
        Pass($"Files listed under /dedup:      {await CountFiles(vfs, "/dedup")}  (expected: 3)");

        // Delete one reference - the blob survives (still referenced twice).
        await vfs.DeleteAsync("/dedup/emails/1234/logo.png");
        Pass($"Refcount after 1 delete:      {await catalog.ReferenceCountAsync(id)}  (expected: 2)");
        Pass($"Blobs after 1 delete:         {await CountFiles(vfs, "/dev/store/blobs")}  (expected: 1)");

        // Delete the rest - the last release garbage-collects the blob.
        await vfs.DeleteAsync("/dedup/emails/5678/logo.png");
        await vfs.DeleteAsync("/dedup/emails/9012/logo.png");
        Pass($"Refcount after all deleted:   {await catalog.ReferenceCountAsync(id)}  (expected: 0)");
        Pass($"Blobs after all deleted:      {await CountFiles(vfs, "/dev/store/blobs")}  (expected: 0)");
        Pass($"Files listed under /dedup:    {await CountFiles(vfs, "/dedup")}  (expected: 0)");

        // InMemoryKvNode does NOT expose a catalog - the capability query returns null.
        var noCatalog = vfs.GetCapability<IVfsCatalog>("/mem/documents/readme.txt");
        Pass($"InMemoryKvNode IVfsCatalog: {noCatalog?.ToString() ?? "null (as expected)"}");
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

    // Recursively lists a subtree and counts the files (ignoring the synthesized directories).
    private static async Task<int> CountFiles(IVirtualFileSystem vfs, string path)
    {
        var count = 0;
        await foreach (var _ in vfs.ListInfoAsync(path, new VfsListOptions { Recurse = true, Kind = VfsEntryKind.Files }))
            count++;
        return count;
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

    protected override IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(VfsNodeRequest req, CancellationToken ct = default)
        => _inner.ListAsync(req, VfsListOptions.Default, ct);

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
            Properties = ImmutableDictionary<string, string?>.Empty
                .Add(VfsPropertyKeys.SymlinkTarget, target.Trim())
        };
    }
}
