using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

// Mounts a SharePoint / OneDrive document library (a Graph "drive") as a VFS path, talking to
// Microsoft Graph over raw HttpClient. You supply the access token via ISharePointTokenProvider;
// this node never sees credentials.
//
// Paths address items by path: drives/{driveId}/root:/{path}:. Folders are real, so listing and
// recursion work naturally. Append is not supported (items are rewritten whole). Beyond the
// standard operations it exposes a delta change feed via GetCapability<ISharePointChangeFeed>.
//
// Optional caching catalog (UseSharePointCachingCatalog): mirror the drive's structure into an
// IVfsCatalog. Directory listings then serve from the local catalog after a fast incremental delta
// sync - the fix for SharePoint's notoriously slow listing of large libraries. Reads and mutations
// still hit SharePoint directly and keep the mirror current.
//
//   services.AddSingleton<ISharePointTokenProvider, MyTokenBridge>();
//   services.AddVirtualFileSystem()
//       .MountSingleton<SharePointNode>("/team",
//           o => o.UseSharePointDrive("b!AbC…").UseSharePointCachingCatalog());
public sealed class SharePointNode : VfsNodeBase, ISharePointChangeFeed, ICatalogMirror
{
    private const long SmallUploadLimit = 4L * 1024 * 1024;        // Graph: single-PUT ceiling
    private const int  ChunkSize        = 320 * 1024 * 10;         // upload-session chunk (mult. of 320 KiB)

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient PlainHttp = new();          // for pre-authed upload-session URLs

    private readonly HttpClient     _http;        // authed Graph client (base https://graph.microsoft.com/v1.0/)
    private readonly string?        _sitePath;    // set when the drive id is resolved lazily from a site
    private readonly string?        _libraryName;
    private readonly string         _rootPath;    // normalized within-drive prefix; "" when none
    private readonly CatalogMirror? _mirror;      // namespace cache; null = no caching
    private readonly ILogger?       _logger;
    private readonly SemaphoreSlim  _driveIdGate = new(1, 1);
    private          string?        _driveId;     // known up-front, or resolved + cached on first use

    // Activated by MountSingleton<SharePointNode>. Targets a drive by id (UseSharePointDrive) or
    // resolves it from a site + library at runtime (UseSharePointSite).
    public SharePointNode(VfsMountOptions options, ISharePointTokenProvider tokens, IServiceProvider services)
        : this(GraphHttp.CreateClient(tokens), Opt(options),
               ResolveMirror(options, services),
               services.GetService<ILogger<SharePointNode>>()) { }

    // Caching is opt-in: UseSharePointCachingCatalog stashes a CatalogSelection. Present = mirror the
    // drive into the selected IVfsCatalog; absent = no caching.
    private static CatalogMirror? ResolveMirror(VfsMountOptions options, IServiceProvider services)
    {
        var sel = options.Get<CatalogSelection>();
        return sel is null ? null : new CatalogMirror(CatalogResolver.Resolve(services, sel.ServiceKey, sel.Partition));
    }

    // Advanced / test seam: a Graph client whose base address is the Graph v1.0 endpoint and that
    // already attaches auth, targeting a drive by id.
    public SharePointNode(HttpClient graphClient, string driveId, string? rootPath = null, CatalogMirror? mirror = null)
        : this(graphClient, new SharePointOptions { DriveId = driveId, RootPath = rootPath }, mirror, null) { }

    // Advanced / test seam: resolve the drive from a site address + library name.
    public static SharePointNode ForSite(
        HttpClient graphClient, string sitePath, string? libraryName = null,
        string? rootPath = null, CatalogMirror? mirror = null, ILogger? logger = null)
        => new(graphClient, new SharePointOptions { SitePath = sitePath, LibraryName = libraryName, RootPath = rootPath },
               mirror, logger);

    private SharePointNode(HttpClient http, SharePointOptions o, CatalogMirror? mirror, ILogger? logger)
    {
        _http        = http ?? throw new ArgumentNullException(nameof(http));
        _driveId     = string.IsNullOrWhiteSpace(o.DriveId) ? null : o.DriveId;
        _sitePath    = NormalizeSiteAddress(o.SitePath);
        _libraryName = o.LibraryName;
        _rootPath    = o.RootPath?.Trim('/') ?? "";
        _mirror      = mirror;
        _logger      = logger;

        if (_driveId is null && string.IsNullOrWhiteSpace(_sitePath))
            throw new ArgumentException(
                "A SharePoint mount needs a drive id (UseSharePointDrive) or a site (UseSharePointSite).");
    }

    private static SharePointOptions Opt(VfsMountOptions o) => o.Require<SharePointOptions>();

    // SharePoint item names are case-insensitive.
    protected override bool IsCaseSensitive => false;

    // -- Drive id (direct, or resolved from a site) ----------------------------

    // Every operation calls this first. With a known drive id it's a no-op; otherwise it resolves
    // the id from the site (once, cached) and nudges the developer toward the direct form.
    private async Task EnsureDriveIdAsync(CancellationToken ct)
    {
        if (_driveId is not null) return;
        await _driveIdGate.WaitAsync(ct);
        try
        {
            if (_driveId is not null) return;
            var id = await ResolveDriveIdAsync(ct);
            _logger?.LogWarning(
                "Resolved the SharePoint drive id for site '{Site}'{Library} to '{DriveId}'. To skip this "
                + "lookup on every start, switch the mount to UseSharePointDrive(\"{DriveId}\").",
                _sitePath, _libraryName is null ? "" : $" (library '{_libraryName}')", id, id);
            _driveId = id;
        }
        finally { _driveIdGate.Release(); }
    }

    private async Task<string> ResolveDriveIdAsync(CancellationToken ct)
    {
        var site   = await _http.GetFromJsonAsync<GraphSite>($"sites/{_sitePath}?$select=id", Json, ct);
        var siteId = site?.Id ?? throw new IOException($"Could not resolve SharePoint site '{_sitePath}'.");

        if (string.IsNullOrEmpty(_libraryName))
        {
            var drive = await _http.GetFromJsonAsync<GraphDrive>($"sites/{siteId}/drive?$select=id", Json, ct);
            return drive?.Id ?? throw new IOException($"Site '{_sitePath}' has no default document library.");
        }

        var page  = await _http.GetFromJsonAsync<GraphDriveCollection>($"sites/{siteId}/drives?$select=id,name", Json, ct);
        var match = page?.Value?.FirstOrDefault(d => string.Equals(d.Name, _libraryName, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? throw new IOException(
            $"Document library '{_libraryName}' not found on site '{_sitePath}'. Available: "
            + $"{string.Join(", ", page?.Value?.Select(d => d.Name) ?? Enumerable.Empty<string?>())}.");
    }

    // Graph addresses a site as "{hostname}:/{server-relative-path}" (or just "{hostname}" for the
    // root site) - not as a URL. Callers often paste the browser URL instead, so accept a full
    // http(s) URL and convert it: "https://contoso.sharepoint.com/sites/Marketing" becomes
    // "contoso.sharepoint.com:/sites/Marketing". Anything already in Graph form (a bare host, or
    // "host:/path" - which parses with a non-http scheme), and null/empty, passes through unchanged.
    internal static string? NormalizeSiteAddress(string? sitePath)
    {
        if (string.IsNullOrWhiteSpace(sitePath)) return sitePath;
        if (!Uri.TryCreate(sitePath, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return sitePath;

        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 ? uri.Host : $"{uri.Host}:/{path}";
    }

    // -- Read ------------------------------------------------------------------

    public override async Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        await EnsureDriveIdAsync(ct);
        var resp = await _http.GetAsync(
            ItemUrl(DrivePath(Rel(request)), "/content"), HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);   // reconcile a stale entry
            return null;
        }
        resp.EnsureSuccessStatusCode();
        if (_mirror is not null) await _mirror.TouchAccessedAsync(request.Path, DateTimeOffset.UtcNow, ct);
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
        await EnsureDriveIdAsync(CancellationToken.None);
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
        await EnsureDriveIdAsync(ct);
        var resp = await _http.DeleteAsync(ItemUrl(DrivePath(Rel(request))), ct);
        if (resp.StatusCode != HttpStatusCode.NotFound) resp.EnsureSuccessStatusCode();
        if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);
    }

    public override async Task RenameAsync(VfsNodeRequest src, string newName, CancellationToken ct = default)
    {
        await EnsureDriveIdAsync(ct);
        var resp = await _http.PatchAsJsonAsync(ItemUrl(DrivePath(Rel(src))), new { name = newName }, Json, ct);
        resp.EnsureSuccessStatusCode();
        if (_mirror is not null) await _mirror.MoveAsync(src.Path, src.Path.WithName(newName), ct);
    }

    public override async Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        await EnsureDriveIdAsync(ct);
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
        await EnsureDriveIdAsync(ct);
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
        await EnsureDriveIdAsync(ct);
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
        await EnsureDriveIdAsync(ct);
        var changes   = new List<SharePointChange>();
        var newCursor = cursor ?? "";
        await foreach (var page in EnumerateDeltaPagesAsync(cursor, ct))
        {
            changes.AddRange(page.Changes);
            if (page.IsComplete && page.Continuation is not null) newCursor = page.Continuation;
        }
        return new SharePointChangeBatch(changes, newCursor);
    }

    // Walk the delta feed one page at a time from startLink (null = a fresh full delta). Each page
    // carries its changes plus a Continuation link and whether it's the terminal page: for a non-final
    // page Continuation is the nextLink (resume/keep paging here); for the final page it's the deltaLink
    // (the cursor for the next incremental sync). Streaming (rather than collecting everything first)
    // lets the seeder apply, checkpoint, and report progress page by page - a large first delta isn't a
    // silent wait, and a crash resumes from the last saved Continuation instead of restarting.
    private async IAsyncEnumerable<(IReadOnlyList<SharePointChange> Changes, string? Continuation, bool IsComplete)>
        EnumerateDeltaPagesAsync(string? startLink, [EnumeratorCancellation] CancellationToken ct)
    {
        var next = startLink ?? $"drives/{_driveId}/root/delta";
        while (next is not null)
        {
            var page    = await _http.GetFromJsonAsync<DriveItemPage>(next, Json, ct);
            var changes = new List<SharePointChange>();
            if (page?.Value is not null)
                foreach (var item in page.Value)
                    if (ToChange(item) is { } change) changes.Add(change);

            if (page?.NextLink is not null) { yield return (changes, page.NextLink, false); next = page.NextLink; }
            else                           { yield return (changes, page?.DeltaLink, true); next = null; }
        }
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

    // Cross-instance sync gate (sizing is deliberately generous; re-up per page means the TTL only has
    // to cover ONE delta page, not the whole delta).
    private static readonly TimeSpan SyncTtl           = TimeSpan.FromSeconds(30);   // lease per re-up
    private static readonly TimeSpan SyncAbortMargin   = TimeSpan.FromSeconds(5);    // winner stops this early
    private static readonly TimeSpan SyncTakeoverGrace = TimeSpan.FromSeconds(3);    // waiter waits this past expiry
    private static readonly TimeSpan SyncPollInterval  = TimeSpan.FromSeconds(2);
    private const int SyncMaxAttempts = 3;

    private static string  ExpiresValue(DateTimeOffset t) => t.ToUnixTimeMilliseconds().ToString();
    private static bool    PastGrace(string v, TimeSpan grace)
        => !long.TryParse(v, out var ms) || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ms + (long)grace.TotalMilliseconds;

    // Incremental delta from the stored cursor, applied one page at a time (bulk upsert/remove = one
    // document write per page), checkpointing the cursor each page so a crash/abort resumes from there.
    // A datetime lease in `sync-expires` gates it across instances: acquire atomically (SetIfNull), re-up
    // the expiry each page, and abort just before the expiry if a page overruns; on success clear the
    // lease so waiters serve at once. A loser waits for the holder to finish (cleared) or die (expired),
    // taking over in that case - bounded by SyncMaxAttempts, then it just serves what's mirrored.
    private async Task SyncAsync(CancellationToken ct)
    {
        var mirror = _mirror!;
        for (var attempt = 0; attempt < SyncMaxAttempts; attempt++)
        {
            var expiresAt = DateTimeOffset.UtcNow + SyncTtl;
            if (await mirror.SetIfNullStateAsync("sync-expires", ExpiresValue(expiresAt), ct))
            {
                var cursor = await mirror.GetStateAsync("cursor", ct);
                var site   = _sitePath ?? _driveId;
                int applied = 0, pages = 0;

                await foreach (var page in EnumerateDeltaPagesAsync(cursor, ct))
                {
                    var deletes = new List<VfsPath>();
                    var upserts = new List<VfsNodeInfo>();
                    foreach (var change in page.Changes)
                        if (change.Type == SharePointChangeType.Deleted) deletes.Add(VfsPath.From(change.Path));
                        else if (change.Info is not null)                upserts.Add(change.Info);

                    if (deletes.Count > 0) await mirror.RemoveAsync(deletes, ct);
                    if (upserts.Count > 0) await mirror.UpsertAsync(upserts, ct);
                    if (page.Continuation is not null) await mirror.SetStateAsync("cursor", page.Continuation, ct);

                    applied += page.Changes.Count;
                    pages++;
                    _logger?.LogDebug("SharePoint delta for '{Site}': page {Page}, {Applied} change(s) applied so far{Done}.",
                        site, pages, applied, page.IsComplete ? " (complete)" : "");

                    if (DateTimeOffset.UtcNow >= expiresAt - SyncAbortMargin) return;   // overran → abort, let the lease lapse
                    expiresAt = DateTimeOffset.UtcNow + SyncTtl;
                    await mirror.SetStateAsync("sync-expires", ExpiresValue(expiresAt), ct);   // re-up
                }

                await mirror.ClearStateAsync("sync-expires", CancellationToken.None);   // done → waiters serve at once
                return;
            }

            // Another instance holds the lease: wait for it to finish (cleared) or die (expired).
            while (true)
            {
                await Task.Delay(SyncPollInterval, ct);
                var v = await mirror.GetStateAsync("sync-expires", ct);
                if (v is null) return;                                                   // finished → serve current mirror
                if (PastGrace(v, SyncTakeoverGrace)) { await mirror.ClearStateAsync("sync-expires", ct); break; }   // died → take over
            }
        }
        // exhausted attempts → serve whatever is mirrored
    }

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
