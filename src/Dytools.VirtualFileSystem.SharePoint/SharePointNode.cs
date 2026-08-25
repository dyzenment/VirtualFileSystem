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

/// <summary>
/// Mounts a SharePoint / OneDrive document library (a Graph "drive") as a VFS path, talking to
/// Microsoft Graph over raw <see cref="HttpClient"/>. You supply the access token via
/// <see cref="ISharePointTokenProvider"/>; this node never sees credentials.
/// <para>
/// Paths address items by path: <c>drives/{driveId}/root:/{path}:</c>. Folders are real, so listing
/// and recursion work naturally. Append is not supported (items are rewritten whole). Beyond the
/// standard operations it exposes a delta change feed via
/// <c>GetCapability&lt;ISharePointChangeFeed&gt;</c>.
/// </para>
/// <para>
/// Optional caching catalog (UseSharePointCachingCatalog): mirror the drive's structure into an
/// <c>IVfsCatalog</c>. Directory listings then serve from the local catalog after a fast incremental
/// delta sync - the fix for SharePoint's notoriously slow listing of large libraries. Reads and
/// mutations still hit SharePoint directly and keep the mirror current.
/// </para>
/// <code>
///   services.AddSingleton&lt;ISharePointTokenProvider, MyTokenBridge&gt;();
///   services.AddVirtualFileSystem()
///       .MountSingleton&lt;SharePointNode&gt;("/team",
///           o =&gt; o.UseSharePointDrive("b!AbC…").UseSharePointCachingCatalog());
/// </code>
/// </summary>
public sealed class SharePointNode : VfsNodeBase, ISharePointChangeFeed, IRefreshableCache
{
    private const long SmallUploadLimit = 4L * 1024 * 1024;        // Graph: single-PUT ceiling
    private const int  ChunkSize        = 320 * 1024 * 10;         // upload-session chunk (mult. of 320 KiB)

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient PlainHttp = new();          // for pre-authed upload-session URLs

    private readonly HttpClient     _http;        // authed Graph client (base https://graph.microsoft.com/v1.0/)
    private readonly string?        _sitePath;    // set when the drive id is resolved lazily from a site
    private readonly string?        _libraryName;
    private readonly string         _rootPath;    // normalized within-drive prefix; "" when none
    private readonly NodeCatalog? _mirror;      // namespace cache; null = no caching
    private readonly ILogger?       _logger;
    private readonly SemaphoreSlim  _driveIdGate = new(1, 1);
    private          string?        _driveId;     // known up-front, or resolved + cached on first use

    /// <summary>
    /// Activated by <c>MountSingleton&lt;SharePointNode&gt;</c>. Targets a drive by id
    /// (UseSharePointDrive) or resolves it from a site + library at runtime (UseSharePointSite).
    /// </summary>
    public SharePointNode(VfsMountOptions options, ISharePointTokenProvider tokens, IServiceProvider services)
        : this(GraphHttp.CreateClient(tokens), Opt(options),
               ResolveMirror(options, services),
               services.GetService<ILogger<SharePointNode>>()) { }

    // Caching is opt-in: UseSharePointCachingCatalog stashes a CatalogSelection. Present = mirror the
    // drive into the selected IVfsCatalog; absent = no caching.
    private static NodeCatalog? ResolveMirror(VfsMountOptions options, IServiceProvider services)
    {
        var sel = options.Get<CatalogSelection>();
        return sel is null ? null : new NodeCatalog(CatalogResolver.Resolve(services, sel.ServiceKey, sel.Partition));
    }

    /// <summary>
    /// Advanced / test seam: a Graph client whose base address is the Graph v1.0 endpoint and that
    /// already attaches auth, targeting a drive by id.
    /// </summary>
    public SharePointNode(HttpClient graphClient, string driveId, string? rootPath = null, NodeCatalog? mirror = null)
        : this(graphClient, new SharePointOptions { DriveId = driveId, RootPath = rootPath }, mirror, null) { }

    /// <summary>Advanced / test seam: resolve the drive from a site address + library name.</summary>
    public static SharePointNode ForSite(
        HttpClient graphClient, string sitePath, string? libraryName = null,
        string? rootPath = null, NodeCatalog? mirror = null, ILogger? logger = null)
        => new(graphClient, new SharePointOptions { SitePath = sitePath, LibraryName = libraryName, RootPath = rootPath },
               mirror, logger);

    private SharePointNode(HttpClient http, SharePointOptions o, NodeCatalog? mirror, ILogger? logger)
    {
        _http        = http ?? throw new ArgumentNullException(nameof(http));
        _driveId     = string.IsNullOrWhiteSpace(o.DriveId) ? null : o.DriveId;
        _sitePath    = NormalizeSiteAddress(o.SitePath);
        _libraryName = o.LibraryName;
        _rootPath    = o.RootPath?.Trim('/') ?? "";
        _mirror      = mirror;
        _logger      = logger;

        // Deletions are matched on the driveItem id, mirrored into CatalogEntry.ContentId. Saying so
        // at mount time beats a cast failure on the first delta that carries a tombstone.
        if (mirror is not null && mirror.Catalog is not IContentAddressedCatalog)
            throw new InvalidOperationException(
                $"A caching SharePoint mount requires a catalog implementing {nameof(IContentAddressedCatalog)}; "
                + $"the registered {mirror.Catalog.GetType().Name} only provides the namespace operations.");

        if (_driveId is null && string.IsNullOrWhiteSpace(_sitePath))
            throw new ArgumentException(
                "A SharePoint mount needs a drive id (UseSharePointDrive) or a site (UseSharePointSite).");
    }

    private static SharePointOptions Opt(VfsMountOptions o) => o.Require<SharePointOptions>();

    /// <summary>SharePoint item names are case-insensitive.</summary>
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

    /// <summary>
    /// Opens the item's content for reading, or returns null if it does not exist (reconciling a
    /// stale mirror entry when caching).
    /// </summary>
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

    /// <summary>
    /// Opens a write stream that uploads the whole item on close. Append is not supported.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when <see cref="VfsWriteOptions.Mode"/> is <see cref="VfsWriteMode.Append"/>; SharePoint items
    /// cannot be appended to and must be rewritten whole.
    /// </exception>
    public override Task<Stream> OpenWriteAsync(
        VfsNodeRequest request, VfsWriteOptions? options = null, CancellationToken ct = default)
    {
        options ??= VfsWriteOptions.Default;

        if (options.Mode == VfsWriteMode.Append)
            throw new NotSupportedException(
                "SharePoint items cannot be appended to; rewrite the whole item instead.");
        return Task.FromResult<Stream>(new SharePointUploadStream(this, DrivePath(Rel(request)), options));
    }

    // Graph accepts full ISO 8601; the round-trip format on a UTC DateTime gives exactly that.
    private static string GraphTime(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    // The fileSystemInfo facet for a write, or null when no timestamps were requested. This is the
    // client-supplied facet, not the driveItem's own service-controlled timestamps - see ToNodeInfo.
    private static Dictionary<string, string>? FileSystemInfoBody(VfsWriteOptions options)
    {
        if (options.ModifiedAt is null && options.CreatedAt is null) return null;

        var facet = new Dictionary<string, string>();
        if (options.ModifiedAt is { } m) facet["lastModifiedDateTime"] = GraphTime(m);
        if (options.CreatedAt  is { } c) facet["createdDateTime"]      = GraphTime(c);
        return facet;
    }

    // Called by SharePointUploadStream on close: pick single-PUT vs chunked upload session, then
    // fold the resulting item into the catalog.
    internal async Task CommitUploadAsync(string drivePath, FileStream temp, VfsWriteOptions options)
    {
        await EnsureDriveIdAsync(CancellationToken.None);
        await temp.FlushAsync();
        temp.Position = 0;
        var conflict = options.Mode == VfsWriteMode.CreateNew ? "fail" : "replace";

        var item = temp.Length < SmallUploadLimit
            ? await UploadSmallAsync(drivePath, temp, conflict, options)
            : await UploadLargeAsync(drivePath, temp, conflict, options);

        if (_mirror is not null && item is not null && StripRoot(drivePath) is { } mountRel)
            await _mirror.UpsertAsync(ToNodeInfo(item, VfsPath.From(mountRel)), CancellationToken.None);
    }

    private async Task<DriveItem?> UploadSmallAsync(
        string drivePath, Stream content, string conflict, VfsWriteOptions options)
    {
        var url  = ItemUrl(drivePath, $"/content?@microsoft.graph.conflictBehavior={conflict}");
        var resp = await _http.PutAsync(url, new StreamContent(content));
        if (conflict == "fail" && resp.StatusCode == HttpStatusCode.Conflict)
            throw new IOException($"SharePoint item already exists: {drivePath}");
        resp.EnsureSuccessStatusCode();
        var item = await resp.Content.ReadFromJsonAsync<DriveItem>(Json);

        // A raw PUT to /content carries no metadata, so requested timestamps need a follow-up PATCH.
        // Only paid for when the caller actually asked for them; the chunked path below gets it free.
        if (FileSystemInfoBody(options) is { } facet)
        {
            var patch = new HttpRequestMessage(HttpMethod.Patch, ItemUrl(drivePath))
            {
                Content = JsonContent.Create(new { fileSystemInfo = facet }, options: Json),
            };
            using var patched = await _http.SendAsync(patch);
            patched.EnsureSuccessStatusCode();
            item = await patched.Content.ReadFromJsonAsync<DriveItem>(Json) ?? item;
        }

        return item;
    }

    private async Task<DriveItem?> UploadLargeAsync(
        string drivePath, Stream content, string conflict, VfsWriteOptions options)
    {
        // The session's item body already travels with the request, so timestamps ride along free here.
        var item0 = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = conflict };
        if (FileSystemInfoBody(options) is { } facet) item0["fileSystemInfo"] = facet;
        var body    = new { item = item0 };
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

    /// <summary>Deletes the item (no-op if already gone), keeping any mirror in step.</summary>
    /// <remarks>
    /// Uniquely among the backends here, this delete is <em>always</em> recoverable: Graph's
    /// <c>DELETE /drive/items/{id}</c> moves the item to the site recycle bin rather than destroying
    /// it, and there is no first-class hard delete to ask for instead. So
    /// <see cref="VfsDeleteDisposition.Recycle"/> is honoured, and
    /// <see cref="VfsDeleteDisposition.Permanent"/> is best-effort - the item still lands in the site
    /// recycle bin, and emptying that is a site-administration matter.
    /// </remarks>
    public override async Task DeleteAsync(
        VfsNodeRequest request, VfsDeleteOptions? options = null, CancellationToken ct = default)
    {
        (options ?? VfsDeleteOptions.Default).ResolveRecycle(available: true, request.Path);

        await EnsureDriveIdAsync(ct);
        var resp = await _http.DeleteAsync(ItemUrl(DrivePath(Rel(request))), ct);
        if (resp.StatusCode != HttpStatusCode.NotFound) resp.EnsureSuccessStatusCode();
        if (_mirror is not null) await _mirror.RemoveAsync(request.Path, ct);
    }

    /// <summary>Renames the item in place, keeping any mirror in step.</summary>
    public override async Task RenameAsync(VfsNodeRequest src, string newName, CancellationToken ct = default)
    {
        await EnsureDriveIdAsync(ct);
        var resp = await _http.PatchAsJsonAsync(ItemUrl(DrivePath(Rel(src))), new { name = newName }, Json, ct);
        resp.EnsureSuccessStatusCode();
        if (_mirror is not null) await _mirror.MoveAsync(src.Path, src.Path.WithName(newName), ct);
    }

    /// <summary>Moves (and possibly renames) the item to a new parent, keeping any mirror in step.</summary>
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

    /// <summary>
    /// Fetches metadata for the item, or null if it does not exist (reconciling a stale mirror entry
    /// when caching). Refreshes the mirror on a hit.
    /// </summary>
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

    /// <summary>
    /// With a caching catalog: sync once (incremental delta), then let the base engine serve the
    /// whole (possibly recursive, filtered) listing from the catalog with no further network calls.
    /// </summary>
    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, VfsListOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureDriveIdAsync(ct);
        if (_mirror is not null) await SyncAsync(ct);
        await foreach (var info in base.ListAsync(request, options ?? VfsListOptions.Default, ct))
            yield return info;
    }

    /// <summary>
    /// Single-level children: from the mirror when caching (sync already ran in <see cref="ListAsync"/>),
    /// else straight from Graph /children.
    /// </summary>
    protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(
        VfsNodeRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_mirror is not null)
        {
            await foreach (var e in _mirror.ListChildrenAsync(request.Path, ct))
                yield return NodeCatalog.ToNodeInfo(e);
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

    /// <inheritdoc/>
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
        if (item.Root is not null) return null;                        // the drive root itself

        // Deletions are tested BEFORE anything path-derived. A tombstone is a minimal object - an id
        // and the deleted facet - so it carries no name and its parentReference has no path. Requiring
        // either first is how every deletion used to be discarded, leaving the mirror append-only.
        if (item.Deleted is not null)
            return item.Id is null
                ? null                                                 // nothing to match it on
                : new SharePointChange(MountRelPath(item), SharePointChangeType.Deleted, null, item.Id);

        // Upserts carry full metadata, and a path is what an upsert is keyed on.
        if (item.Name is null) return null;
        if (MountRelPath(item) is not { } mountRel) return null;       // outside this mount's root

        return new SharePointChange(
            mountRel, SharePointChangeType.Updated, ToNodeInfo(item, VfsPath.From(mountRel)), item.Id);
    }

    // Mount-relative path for an item, or null when Graph reported no resolvable parent path (a
    // tombstone) or the item sits outside this mount's root.
    private string? MountRelPath(DriveItem item)
    {
        if (item.Name is null) return null;
        if (ParentRelPath(item.ParentReference?.Path) is not { } parentRel) return null;

        return StripRoot(parentRel.Length == 0 ? item.Name : $"{parentRel}/{item.Name}");
    }

    // -- Content hashes --------------------------------------------------------

    /// <summary>
    /// Hashing is bound to the entry asked for, so the capability itself takes no path. Recycling is
    /// always available here - see <see cref="DeleteAsync"/> for why it is not optional.
    /// </summary>
    public override T? GetEntryCapability<T>(VfsPath relativePath) where T : class
        => typeof(T) == typeof(IRecycling)
            ? new AlwaysRecycling() as T
            : new SharePointEntryHashing(this, relativePath) as T;

    // Serves SharePointEntryHashing.
    internal async Task<string?> GetEntryHashAsync(
        VfsPath path, string algorithm, VfsHashBudget budget, CancellationToken ct)
    {
        var key = VfsPropertyKeys.HashKey(algorithm);

        // The delta feed carries Graph's hashes into the catalog with the rest of Properties, so a
        // cached node answers from the row it already has - no request at all.
        if (_mirror is not null && await _mirror.GetAsync(path, ct) is { } entry
            && entry.Properties?.TryGetValue(key, out var mirrored) == true
            && !string.IsNullOrEmpty(mirrored))
            return mirrored;

        if (budget < VfsHashBudget.Fetch) return null;

        // One request per entry - fine for a file, wrong for a loop over a library.
        var info = await GetInfoAsync(new VfsNodeRequest(path), ct);
        if (info?.Properties.TryGetValue(key, out var fetched) == true && !string.IsNullOrEmpty(fetched))
            return fetched;

        // Graph reports quickXor and nothing else here, so any other algorithm means downloading.
        if (budget < VfsHashBudget.Compute) return null;

        string? computed;
        await using (var content = await OpenReadAsync(new VfsNodeRequest(path), ct))
        {
            if (content is null) return null;
            computed = await VfsHashing.ComputeAsync(content, algorithm, ct);
        }

        // Park it on the mirrored row - our own cache, so no opt-in. A driveItem has no arbitrary
        // metadata slot, so there is nowhere in SharePoint itself to write it and ComputeAndStore
        // behaves as Compute does.
        if (computed is not null && _mirror is not null)
            await _mirror.SetPropertyAsync(path, key, computed, ct);

        return computed;
    }

    // -- Catalog mirror sync ---------------------------------------------------

    /// <summary>
    /// Force a delta sync of the mirror (<c>IRefreshableCache</c>). Listing already syncs, so this is for
    /// callers that want an explicit refresh without listing.
    /// </summary>
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
                    var deletedIds = new List<string>();
                    var upserts    = new List<VfsNodeInfo>();
                    foreach (var change in page.Changes)
                        if (change.Type == SharePointChangeType.Deleted) { if (change.Id is { } id) deletedIds.Add(id); }
                        else if (change.Info is not null)                upserts.Add(change.Info);

                    // Deletions first, so an item deleted and recreated inside one page ends up present.
                    if (deletedIds.Count > 0) await RemoveByItemIdAsync(mirror, deletedIds, ct);
                    if (upserts.Count > 0)
                    {
                        await RemoveStaleAliasesAsync(mirror, upserts, ct);
                        await mirror.UpsertAsync(upserts, ct);
                    }
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

    // Removes mirrored entries by driveItem id. The id rides in CatalogEntry.ContentId (see
    // ToNodeInfo), so each tombstone is an indexed lookup rather than a namespace scan. An id nothing
    // is mirrored under is simply a deletion outside this mount's root - the delta feed is drive-wide,
    // and a tombstone carries no path to filter on beforehand.
    private async Task RemoveByItemIdAsync(NodeCatalog mirror, List<string> itemIds, CancellationToken ct)
    {
        var catalog = (IContentAddressedCatalog)mirror.Catalog;
        var paths   = new List<VfsPath>();

        foreach (var id in itemIds)
        {
            var before = paths.Count;
            await foreach (var entry in catalog.ListByContentIdAsync(id, ct))
                paths.Add(entry.Path);

            // One id at several paths means the mirror drifted - remove them all and say so, rather
            // than leaving whichever copy the lookup happened not to return.
            if (paths.Count - before > 1)
                _logger?.LogWarning(
                    "SharePoint item '{ItemId}' is mirrored at {Count} paths; removing all of them.",
                    id, paths.Count - before);
        }

        if (paths.Count > 0) await mirror.RemoveAsync(paths, ct);
    }

    // Drops any mirrored row holding an incoming item's id at some OTHER path, leaving the incoming
    // path to be upserted. Two cases collapse into one here: a rename or move made outside the VFS,
    // which Graph reports as an upsert at the new path and never mentions the old one - and genuine
    // drift, where an id ended up on more than one row. Either way one item means one row.
    private async Task RemoveStaleAliasesAsync(NodeCatalog mirror, List<VfsNodeInfo> upserts, CancellationToken ct)
    {
        var catalog = (IContentAddressedCatalog)mirror.Catalog;
        List<VfsPath>? stale = null;

        foreach (var info in upserts)
        {
            if (info.Properties.GetString(VfsPropertyKeys.ContentId) is not { } id) continue;

            await foreach (var entry in catalog.ListByContentIdAsync(id, ct))
                if (!entry.Path.Equals(info.RelativePath))
                    (stale ??= []).Add(entry.Path);
        }

        if (stale is null) return;

        _logger?.LogDebug(
            "SharePoint delta: dropping {Count} mirrored row(s) whose item now lives elsewhere.", stale.Count);
        await mirror.RemoveAsync(stale, ct);
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

        // The driveItem id, carried as the entry's content id: for this mirror the "storage key for
        // the bytes" IS Graph's handle for the item, and unlike the path it survives a rename and is
        // all a delta tombstone reports. NodeCatalog lifts this key into CatalogEntry.ContentId,
        // which is the indexed column, so a deletion resolves without scanning the namespace.
        if (item.Id is not null)           props = props.Add(VfsPropertyKeys.ContentId, item.Id);
        if (item.ETag is not null)         props = props.Add("ETag", item.ETag);
        if (item.File?.MimeType is { } mt) props = props.Add("ContentType", mt);
        if (item.WebUrl is not null)       props = props.Add("WebUrl", item.WebUrl);

        // Carry whatever hashes Graph reported. These travel through the delta feed into the catalog
        // with everything else in Properties, so a mirrored listing can answer "what is this file's
        // hash" without a round-trip per file.
        if (item.File?.Hashes is { } h)
        {
            if (h.QuickXorHash is { Length: > 0 } qx) props = props.Add(VfsPropertyKeys.HashKey(VfsHashAlgorithms.QuickXor), qx);
            if (h.Sha1Hash     is { Length: > 0 } s1) props = props.Add(VfsPropertyKeys.HashKey(VfsHashAlgorithms.Sha1),     s1);
            if (h.Sha256Hash   is { Length: > 0 } s2) props = props.Add(VfsPropertyKeys.HashKey(VfsHashAlgorithms.Sha256),   s2);
            if (h.Crc32Hash    is { Length: > 0 } c3) props = props.Add(VfsPropertyKeys.HashKey(VfsHashAlgorithms.Crc32),    c3);
        }

        // The service-controlled timestamps stay reachable for callers who want "when did the
        // library last change", as opposed to "when was this file last modified".
        if (item.LastModifiedDateTime is { } serverModified)
            props = props.Add("ServerModified", serverModified.ToString("O"));
        if (item.CreatedDateTime is { } serverCreated)
            props = props.Add("ServerCreated", serverCreated.ToString("O"));

        return new VfsNodeInfo
        {
            RelativePath = relativePath,
            IsFile       = !isDir,
            IsDirectory  = isDir,
            SizeBytes    = isDir ? null : item.Size,
            // fileSystemInfo first: it is the file's own mtime, the direct analogue of every other
            // backend's, and the only value that survives a round-trip through a write. Falls back to
            // the service value for items that never carried one.
            CreatedAt    = item.FileSystemInfo?.CreatedDateTime ?? item.CreatedDateTime,
            ModifiedAt   = item.FileSystemInfo?.LastModifiedDateTime ?? item.LastModifiedDateTime,
            Properties   = props,
        };
    }
}

/// <summary>
/// A <see cref="SharePointNode"/>'s hashing bound to one entry - what
/// <c>GetEntryCapability&lt;IContentHashing&gt;</c> hands back.
/// </summary>
internal sealed class SharePointEntryHashing(SharePointNode node, VfsPath path) : IContentHashing
{
    /// <summary>
    /// SharePoint and OneDrive for Business report quickXorHash; personal OneDrive reports sha1/sha256
    /// instead. Only quickXor is advertised because that is what this node is pointed at, though
    /// <see cref="GetHashAsync"/> returns any algorithm Graph actually supplied.
    /// </summary>
    public IReadOnlyList<string> NativeAlgorithms { get; } = [VfsHashAlgorithms.QuickXor];

    /// <summary>
    /// </summary>
    /// <summary>
    /// The standard algorithms, by downloading the item. Advertised because the caller has to opt in
    /// with <see cref="VfsHashBudget.Compute"/> - it is never reached by accident.
    /// </summary>
    public IReadOnlyList<string> ComputableAlgorithms { get; } = VfsHashing.Computable;

    /// <inheritdoc/>
    public Task<string?> GetHashAsync(
        string algorithm, VfsHashBudget budget = VfsHashBudget.Fetch, CancellationToken ct = default)
        => node.GetEntryHashAsync(path, algorithm, budget, ct);
}


/// <summary>
/// A backend whose delete is recoverable for every entry, unconditionally - SharePoint's site
/// recycle bin. Stateless, so it needs no binding to the entry it was asked about.
/// </summary>
internal sealed class AlwaysRecycling : IRecycling
{
    /// <inheritdoc/>
    public ValueTask<bool> CanRecycleAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
}
