using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

// Mounts a SharePoint / OneDrive document library (a Graph "drive") as a VFS path, talking to
// Microsoft Graph over raw HttpClient. You supply the access token via ISharePointTokenProvider;
// this node never sees credentials.
//
// Paths address items by path: drives/{driveId}/root:/{path}:. Folders are real, so listing and
// recursion work naturally. Append is not supported (items are rewritten whole). Beyond the
// standard operations it exposes a delta change feed via GetCapability<ISharePointChangeFeed>.
//
// Optional caching catalog (UseCachingCatalog): mirror the drive's structure into an IVfsCatalog.
// Directory listings then serve from the local catalog after a fast incremental delta sync - the
// fix for SharePoint's notoriously slow listing of large libraries. Reads and mutations still hit
// SharePoint directly and keep the mirror current.
//
//   services.AddSingleton<ISharePointTokenProvider, MyTokenBridge>();
//   services.AddVirtualFileSystem()
//       .MountSingleton<SharePointNode>("/team",
//           o => o.UseSharePointDrive("b!AbC…").UseCachingCatalog());
public sealed class SharePointNode : VfsNodeBase, ISharePointChangeFeed, ICatalogMirror
{
    private const long SmallUploadLimit = 4L * 1024 * 1024;        // Graph: single-PUT ceiling
    private const int  ChunkSize        = 320 * 1024 * 10;         // upload-session chunk (mult. of 320 KiB)

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient PlainHttp = new();          // for pre-authed upload-session URLs

    private readonly HttpClient     _http;      // authed Graph client (base https://graph.microsoft.com/v1.0/)
    private readonly string         _driveId;
    private readonly string         _rootPath;  // normalized within-drive prefix; "" when none
    private readonly CatalogMirror? _mirror;    // namespace cache; null = no caching

    // Activated by MountSingleton<SharePointNode> from the options, the DI token provider, and DI.
    public SharePointNode(VfsMountOptions options, ISharePointTokenProvider tokens, IServiceProvider services)
        : this(GraphHttp.CreateClient(tokens),
               options.Require<SharePointOptions>().DriveId,
               options.Require<SharePointOptions>().RootPath,
               CatalogMirror.FromOptions(options.Require<SharePointOptions>().UseCatalog, options, services)) { }

    // Advanced / test seam: supply a Graph client whose base address is the Graph v1.0 endpoint
    // and that already attaches auth, and (optionally) a mirror to cache into.
    public SharePointNode(HttpClient graphClient, string driveId, string? rootPath = null, CatalogMirror? mirror = null)
    {
        _http    = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
        _driveId = string.IsNullOrWhiteSpace(driveId)
            ? throw new ArgumentException("A Graph drive id is required.", nameof(driveId))
            : driveId;
        _rootPath = rootPath?.Trim('/') ?? "";
        _mirror   = mirror;
    }

    // SharePoint item names are case-insensitive.
    protected override bool IsCaseSensitive => false;

    // -- Read ------------------------------------------------------------------

    public override async Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            ItemUrl(DrivePath(Rel(request)), "/content"), HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);   // reconcile a stale entry
            return null;
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    // -- Write -----------------------------------------------------------------

    public override Task<Stream> OpenWriteAsync(
        VfsNodeRequest request, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
    {
        if (mode == VfsWriteMode.Append)
            throw new NotSupportedException(
                "SharePoint items cannot be appended to; rewrite the whole item instead.");
        return Task.FromResult<Stream>(new SharePointUploadStream(this, DrivePath(Rel(request)), mode));
    }

    // Called by SharePointUploadStream on close: pick single-PUT vs chunked upload session, then
    // fold the resulting item into the catalog.
    internal async Task CommitUploadAsync(string drivePath, FileStream temp, VfsWriteMode mode)
    {
        await temp.FlushAsync();
        temp.Position = 0;
        var conflict = mode == VfsWriteMode.CreateNew ? "fail" : "replace";

        var item = temp.Length < SmallUploadLimit
            ? await UploadSmallAsync(drivePath, temp, conflict)
            : await UploadLargeAsync(drivePath, temp, conflict);

        if (_mirror is not null && item is not null && StripRoot(drivePath) is { } mountRel)
            await _mirror.UpsertAsync(ToNodeInfo(item, VfsPath.From(mountRel)), CancellationToken.None);
    }

    private async Task<DriveItem?> UploadSmallAsync(string drivePath, Stream content, string conflict)
    {
        var url  = ItemUrl(drivePath, $"/content?@microsoft.graph.conflictBehavior={conflict}");
        var resp = await _http.PutAsync(url, new StreamContent(content));
        if (conflict == "fail" && resp.StatusCode == HttpStatusCode.Conflict)
            throw new IOException($"SharePoint item already exists: {drivePath}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<DriveItem>(Json);
    }

    private async Task<DriveItem?> UploadLargeAsync(string drivePath, Stream content, string conflict)
    {
        var body    = new { item = new Dictionary<string, string> { ["@microsoft.graph.conflictBehavior"] = conflict } };
        var create  = await _http.PostAsJsonAsync(ItemUrl(drivePath, "/createUploadSession"), body, Json);
        create.EnsureSuccessStatusCode();
        var session = await create.Content.ReadFromJsonAsync<UploadSession>(Json);
        var uploadUrl = session?.UploadUrl ?? throw new IOException("Graph did not return an upload URL.");

        var total  = content.Length;
        var buffer = new byte[ChunkSize];
        long offset = 0;
        DriveItem? result = null;
        while (offset < total)
        {
            var read = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
            using var chunk = new ByteArrayContent(buffer, 0, read);
            chunk.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, offset + read - 1, total);

            // The upload URL is pre-authenticated - send it without the bearer.
            using var req  = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = chunk };
            using var resp = await PlainHttp.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                result = await resp.Content.ReadFromJsonAsync<DriveItem>(Json);
            offset += read;
        }
        return result;
    }

    // -- Delete / Rename / Move (native, catalog kept in step) -----------------

    public override async Task DeleteAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync(ItemUrl(DrivePath(Rel(request))), ct);
        if (resp.StatusCode != HttpStatusCode.NotFound) resp.EnsureSuccessStatusCode();
        if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);
    }

    public override async Task RenameAsync(VfsNodeRequest src, string newName, CancellationToken ct = default)
    {
        var resp = await _http.PatchAsJsonAsync(ItemUrl(DrivePath(Rel(src))), new { name = newName }, Json, ct);
        resp.EnsureSuccessStatusCode();
        if (_mirror is not null) await _mirror.MoveAsync(src.Path, src.Path.WithName(newName), ct);
    }

    public override async Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        var dstDrive = DrivePath(Rel(dst));
        var slash    = dstDrive.LastIndexOf('/');
        var parent   = slash < 0 ? "" : dstDrive[..slash];
        var name     = slash < 0 ? dstDrive : dstDrive[(slash + 1)..];
        var parentRefPath = parent.Length == 0
            ? $"/drives/{_driveId}/root:"
            : $"/drives/{_driveId}/root:/{EscapePath(parent)}";

        var body = new { parentReference = new { path = parentRefPath }, name };
        var resp = await _http.PatchAsJsonAsync(ItemUrl(DrivePath(Rel(src))), body, Json, ct);
        resp.EnsureSuccessStatusCode();
        if (_mirror is not null) await _mirror.MoveAsync(src.Path, dst.Path, ct);
    }

    // CopyAsync is intentionally left to the VfsNodeBase stream fallback: Graph's native copy is
    // asynchronous (202 + a monitor URL to poll), which isn't worth the complexity here yet.

    // -- Metadata --------------------------------------------------------------

    public override async Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(ItemUrl(DrivePath(Rel(request))), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var item = await resp.Content.ReadFromJsonAsync<DriveItem>(Json, ct);
        if (item is null) return null;

        var info = ToNodeInfo(item, request.Path);
        if (_mirror is not null) await _mirror.UpsertAsync(info, ct);
        return info;
    }

    // -- Listing ---------------------------------------------------------------

    // With a caching catalog: sync once (incremental delta), then let the base engine serve the
    // whole (possibly recursive, filtered) listing from the catalog with no further network calls.
    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, VfsListOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_mirror is not null) await SyncAsync(ct);
        await foreach (var info in base.ListAsync(request, options ?? VfsListOptions.Default, ct))
            yield return info;
    }

    // Single-level children: from the mirror when caching (sync already ran in ListAsync), else
    // straight from Graph /children.
    protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(
        VfsNodeRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_mirror is not null)
        {
            await foreach (var e in _mirror.ListChildrenAsync(request.Path, ct))
                yield return CatalogMirror.ToNodeInfo(e);
            yield break;
        }

        var next = ItemUrl(DrivePath(Rel(request)), "/children");
        while (next is not null)
        {
            var page = await _http.GetFromJsonAsync<DriveItemPage>(next, Json, ct);
            if (page?.Value is null) yield break;

            foreach (var item in page.Value)
            {
                if (item.Name is null) continue;
                var childPath = request.Path.PathSpan.IsEmpty
                    ? VfsPath.From(item.Name)
                    : VfsPath.From(request.Path, item.Name);
                yield return ToNodeInfo(item, childPath);
            }
            next = page.NextLink;
        }
    }

    // -- Delta change feed (ISharePointChangeFeed) -----------------------------

    public async Task<SharePointChangeBatch> GetChangesAsync(string? cursor, CancellationToken ct = default)
    {
        var next      = cursor ?? $"drives/{_driveId}/root/delta";
        var changes   = new List<SharePointChange>();
        var newCursor = cursor ?? "";

        while (next is not null)
        {
            var page = await _http.GetFromJsonAsync<DriveItemPage>(next, Json, ct);
            if (page?.Value is not null)
                foreach (var item in page.Value)
                    if (ToChange(item) is { } change) changes.Add(change);

            if (page?.NextLink is not null) { next = page.NextLink; continue; }
            newCursor = page?.DeltaLink ?? newCursor;
            next = null;
        }
        return new SharePointChangeBatch(changes, newCursor);
    }

    private SharePointChange? ToChange(DriveItem item)
    {
        if (item.Root is not null || item.Name is null) return null;   // the drive root itself

        var parentRel = ParentRelPath(item.ParentReference?.Path);
        if (parentRel is null) return null;
        var drivePath = parentRel.Length == 0 ? item.Name : $"{parentRel}/{item.Name}";

        var mountRel = StripRoot(drivePath);
        if (mountRel is null) return null;                             // outside this mount's root

        return item.Deleted is not null
            ? new SharePointChange(mountRel, SharePointChangeType.Deleted, null)
            : new SharePointChange(mountRel, SharePointChangeType.Updated, ToNodeInfo(item, VfsPath.From(mountRel)));
    }

    // -- Catalog mirror sync ---------------------------------------------------

    // Force a delta sync of the mirror (ICatalogMirror). Listing already syncs, so this is for
    // callers that want an explicit refresh without listing.
    public Task RefreshAsync(CancellationToken ct = default) => _mirror is null ? Task.CompletedTask : SyncAsync(ct);

    // Incremental delta from the stored cursor, applied through the shared mirror.
    private Task SyncAsync(CancellationToken ct) => _mirror!.SyncAsync(async token =>
    {
        var cursor = await _mirror.GetStateAsync("cursor", token);
        var batch  = await GetChangesAsync(cursor, token);

        foreach (var change in batch.Changes)
        {
            if (change.Type == SharePointChangeType.Deleted) await _mirror.RemoveAsync(VfsPath.From(change.Path), token);
            else if (change.Info is not null)                await _mirror.UpsertAsync(change.Info, token);
        }

        if (!string.IsNullOrEmpty(batch.Cursor)) await _mirror.SetStateAsync("cursor", batch.Cursor, token);
    }, ct);

    // -- Helpers ---------------------------------------------------------------

    private static string Rel(VfsNodeRequest request) => new(request.Path.PathSpan);

    private string DrivePath(string rel)
        => _rootPath.Length == 0 ? rel : rel.Length == 0 ? _rootPath : $"{_rootPath}/{rel}";

    private string ItemUrl(string drivePath, string suffix = "")
        => drivePath.Length == 0
            ? $"drives/{_driveId}/root{suffix}"
            : $"drives/{_driveId}/root:/{EscapePath(drivePath)}:{suffix}";

    private static string EscapePath(string drivePath)
        => string.Join('/', drivePath.Split('/').Select(Uri.EscapeDataString));

    // "/drives/{id}/root:/A/B" → "A/B"; "/drives/{id}/root:" → ""; null/unknown → null.
    private static string? ParentRelPath(string? graphPath)
    {
        if (graphPath is null) return null;
        var marker = graphPath.IndexOf("root:", StringComparison.Ordinal);
        if (marker < 0) return null;
        return Uri.UnescapeDataString(graphPath[(marker + "root:".Length)..].Trim('/'));
    }

    // Drive-relative path → mount-relative (strip the root prefix), or null if outside it.
    private string? StripRoot(string drivePath)
    {
        if (_rootPath.Length == 0) return drivePath;
        if (drivePath.Equals(_rootPath, StringComparison.OrdinalIgnoreCase)) return "";
        return drivePath.StartsWith(_rootPath + "/", StringComparison.OrdinalIgnoreCase)
            ? drivePath[(_rootPath.Length + 1)..]
            : null;
    }

    private static VfsNodeInfo ToNodeInfo(DriveItem item, VfsPath relativePath)
    {
        var isDir = item.Folder is not null || item.Root is not null;

        var props = ImmutableDictionary<string, string?>.Empty;
        if (item.ETag is not null)         props = props.Add("ETag", item.ETag);
        if (item.File?.MimeType is { } mt) props = props.Add("ContentType", mt);
        if (item.WebUrl is not null)       props = props.Add("WebUrl", item.WebUrl);

        return new VfsNodeInfo
        {
            RelativePath = relativePath,
            IsFile       = !isDir,
            IsDirectory  = isDir,
            SizeBytes    = isDir ? null : item.Size,
            CreatedAt    = item.CreatedDateTime,
            ModifiedAt   = item.LastModifiedDateTime,
            Properties   = props,
        };
    }
}
