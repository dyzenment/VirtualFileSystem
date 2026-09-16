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
    public SharePointNode(
        HttpClient graphClient, string driveId, string? rootPath = null, NodeCatalog? mirror = null, ILogger? logger = null)
        : this(graphClient, new SharePointOptions { DriveId = driveId, RootPath = rootPath }, mirror, logger) { }

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
        await _driveIdGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_driveId is not null) return;

            string id;
            try { id = await ResolveDriveIdAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
            {
                // No path yet - this fails before any entry is addressed.
                throw GraphErrors.Transport(ex, VfsOperation.Resolve, null, null, ct);
            }

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
        var site   = await _http.GetFromJsonAsync<GraphSite>($"sites/{_sitePath}?$select=id", Json, ct)
                               .ConfigureAwait(false);
        var siteId = site?.Id ?? throw new VfsException(
            VfsFailureReason.NotFound, VfsOperation.Resolve,
            message: $"Could not resolve SharePoint site '{_sitePath}'.");

        if (string.IsNullOrEmpty(_libraryName))
        {
            var drive = await _http.GetFromJsonAsync<GraphDrive>($"sites/{siteId}/drive?$select=id", Json, ct)
                                   .ConfigureAwait(false);
            return drive?.Id ?? throw new VfsException(
                VfsFailureReason.NotFound, VfsOperation.Resolve,
                message: $"Site '{_sitePath}' has no default document library.");
        }

        var page  = await _http.GetFromJsonAsync<GraphDriveCollection>($"sites/{siteId}/drives?$select=id,name", Json, ct)
                               .ConfigureAwait(false);
        var match = page?.Value?.FirstOrDefault(d => string.Equals(d.Name, _libraryName, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? throw new VfsException(
            VfsFailureReason.NotFound, VfsOperation.Resolve,
            message: $"Document library '{_libraryName}' not found on site '{_sitePath}'. Available: "
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

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(
                ItemUrl(DrivePath(Rel(request)), "/content"), HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
        {
            throw Graph(ex, VfsOperation.Read, request, ct);
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            // Reconcile a stale entry: absence is an answer here, not a failure.
            await MirrorAsync(m => m.RemoveAsync(request.Path, ct), VfsOperation.Read, request, ct);
            return null;
        }

        await EnsureOkAsync(resp, VfsOperation.Read, request, ct);
        await MirrorAsync(m => m.TouchAccessedAsync(request.Path, DateTimeOffset.UtcNow, ct), VfsOperation.Read, request, ct);

        // The stream itself is NOT guarded: a connection dropped mid-read still throws the
        // backend's own exception. Closing that gap needs a mapping Stream decorator.
        try { return await resp.Content.ReadAsStreamAsync(ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Graph(ex, VfsOperation.Read, request, ct); }
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

        // The upload happens on close, outside every method the pipeline guards, so the stream has
        // to carry the VFS path with it - by then the request is long gone.
        return Task.FromResult<Stream>(new SharePointUploadStream(
            this, DrivePath(Rel(request)), options, VfsFailure.FullPath(request), VfsFailure.MountOf(request)));
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
    internal async Task CommitUploadAsync(
        string drivePath, FileStream temp, VfsWriteOptions options, string? vfsPath, string? mount)
    {
        await EnsureDriveIdAsync(CancellationToken.None).ConfigureAwait(false);
        await temp.FlushAsync().ConfigureAwait(false);
        temp.Position = 0;
        var conflict = options.Mode == VfsWriteMode.CreateNew ? "fail" : "replace";

        DriveItem? item;
        try
        {
            item = temp.Length < SmallUploadLimit
                ? await UploadSmallAsync(drivePath, temp, conflict, options, vfsPath, mount).ConfigureAwait(false)
                : await UploadLargeAsync(drivePath, temp, conflict, options, vfsPath, mount).ConfigureAwait(false);
        }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, CancellationToken.None))
        {
            throw GraphErrors.Transport(ex, VfsOperation.Write, vfsPath, mount, CancellationToken.None);
        }

        if (item is not null && StripRoot(drivePath) is { } mountRel)
            await MirrorAsync(
                m => m.UpsertAsync(ToNodeInfo(item, VfsPath.From(mountRel)), CancellationToken.None),
                VfsOperation.Write, vfsPath, mount, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<DriveItem?> UploadSmallAsync(
        string drivePath, Stream content, string conflict, VfsWriteOptions options, string? vfsPath, string? mount)
    {
        var url  = ItemUrl(drivePath, $"/content?@microsoft.graph.conflictBehavior={conflict}");
        var resp = await _http.PutAsync(url, new StreamContent(content)).ConfigureAwait(false);
        if (conflict == "fail" && resp.StatusCode == HttpStatusCode.Conflict)
            throw new VfsException(
                VfsFailureReason.Conflict, VfsOperation.Write, vfsPath, mount,
                $"SharePoint item already exists: {drivePath}");
        await resp.EnsureOkAsync(VfsOperation.Write, vfsPath, mount, CancellationToken.None).ConfigureAwait(false);
        var item = await resp.Content.ReadFromJsonAsync<DriveItem>(Json).ConfigureAwait(false);

        // A raw PUT to /content carries no metadata, so requested timestamps need a follow-up PATCH.
        // Only paid for when the caller actually asked for them; the chunked path below gets it free.
        if (FileSystemInfoBody(options) is { } facet)
        {
            var patch = new HttpRequestMessage(HttpMethod.Patch, ItemUrl(drivePath))
            {
                Content = JsonContent.Create(new { fileSystemInfo = facet }, options: Json),
            };
            using var patched = await _http.SendAsync(patch).ConfigureAwait(false);
            await patched.EnsureOkAsync(VfsOperation.Write, vfsPath, mount, CancellationToken.None).ConfigureAwait(false);
            item = await patched.Content.ReadFromJsonAsync<DriveItem>(Json).ConfigureAwait(false) ?? item;
        }

        return item;
    }

    private async Task<DriveItem?> UploadLargeAsync(
        string drivePath, Stream content, string conflict, VfsWriteOptions options, string? vfsPath, string? mount)
    {
        // The session's item body already travels with the request, so timestamps ride along free here.
        var item0 = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = conflict };
        if (FileSystemInfoBody(options) is { } facet) item0["fileSystemInfo"] = facet;
        var body    = new { item = item0 };
        var create  = await _http.PostAsJsonAsync(ItemUrl(drivePath, "/createUploadSession"), body, Json)
                                 .ConfigureAwait(false);
        await create.EnsureOkAsync(VfsOperation.Write, vfsPath, mount, CancellationToken.None).ConfigureAwait(false);
        var session = await create.Content.ReadFromJsonAsync<UploadSession>(Json).ConfigureAwait(false);
        var uploadUrl = session?.UploadUrl ?? throw new VfsException(
            VfsFailureReason.Unknown, VfsOperation.Write, vfsPath, mount,
            "Graph did not return an upload URL.");

        var total  = content.Length;
        var buffer = new byte[ChunkSize];
        long offset = 0;
        DriveItem? result = null;
        while (offset < total)
        {
            var read = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false)
                                    .ConfigureAwait(false);
            using var chunk = new ByteArrayContent(buffer, 0, read);
            chunk.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, offset + read - 1, total);

            // The upload URL is pre-authenticated - send it without the bearer.
            using var req  = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = chunk };
            using var resp = await PlainHttp.SendAsync(req).ConfigureAwait(false);
            await resp.EnsureOkAsync(VfsOperation.Write, vfsPath, mount, CancellationToken.None).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                result = await resp.Content.ReadFromJsonAsync<DriveItem>(Json).ConfigureAwait(false);
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

        HttpResponseMessage resp;
        try { resp = await _http.DeleteAsync(ItemUrl(DrivePath(Rel(request))), ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Graph(ex, VfsOperation.Delete, request, ct); }

        // Already gone is a no-op, so 404 is not a failure here.
        if (resp.StatusCode != HttpStatusCode.NotFound)
            await EnsureOkAsync(resp, VfsOperation.Delete, request, ct);

        await MirrorAsync(m => m.RemoveAsync(request.Path, ct), VfsOperation.Delete, request, ct);
    }

    /// <summary>Renames the item in place, keeping any mirror in step.</summary>
    public override async Task RenameAsync(VfsNodeRequest src, string newName, CancellationToken ct = default)
    {
        await EnsureDriveIdAsync(ct);

        HttpResponseMessage resp;
        try { resp = await _http.PatchAsJsonAsync(ItemUrl(DrivePath(Rel(src))), new { name = newName }, Json, ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Graph(ex, VfsOperation.Rename, src, ct); }

        await EnsureOkAsync(resp, VfsOperation.Rename, src, ct);
        await MirrorAsync(m => m.MoveAsync(src.Path, src.Path.WithName(newName), ct), VfsOperation.Rename, src, ct);
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

        HttpResponseMessage resp;
        try { resp = await _http.PatchAsJsonAsync(ItemUrl(DrivePath(Rel(src))), body, Json, ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Graph(ex, VfsOperation.Move, src, ct); }

        await EnsureOkAsync(resp, VfsOperation.Move, src, ct);
        await MirrorAsync(m => m.MoveAsync(src.Path, dst.Path, ct), VfsOperation.Move, src, ct);
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

        DriveItem? item;
        try
        {
            var resp = await _http.GetAsync(ItemUrl(DrivePath(Rel(request))), ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // Reconcile a stale entry: absence is an answer here, not a failure.
                await MirrorAsync(m => m.RemoveAsync(request.Path, ct), VfsOperation.GetInfo, request, ct);
                return null;
            }

            await EnsureOkAsync(resp, VfsOperation.GetInfo, request, ct);
            item = await resp.Content.ReadFromJsonAsync<DriveItem>(Json, ct);
        }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
        {
            throw Graph(ex, VfsOperation.GetInfo, request, ct);
        }

        if (item is null) return null;

        var info = ToNodeInfo(item, request.Path);
        await MirrorAsync(m => m.UpsertAsync(info, ct), VfsOperation.GetInfo, request, ct);
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

        if (_mirror is not null)
        {
            try { await SyncAsync(ct); }
            catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Graph(ex, VfsOperation.Sync, request, ct); }
        }

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
        // Both branches drive their enumerator by hand: C# forbids a yield return inside a try that
        // has a catch, so each step is fetched in the try and yielded outside it. Nothing is
        // buffered, so entries already produced still reach the caller before a failure does.
        if (_mirror is not null)
        {
            await using var entries = _mirror.ListChildrenAsync(request.Path, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                bool moved;
                try { moved = await entries.MoveNextAsync(); }
                catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
                {
                    throw VfsFailure.Wrap(ex, VfsOperation.List, request, VfsFailureOrigin.Catalog);
                }

                if (!moved) yield break;
                yield return NodeCatalog.ToNodeInfo(entries.Current);
            }
        }

        var next = ItemUrl(DrivePath(Rel(request)), "/children");
        while (next is not null)
        {
            DriveItemPage? page;
            try { page = await _http.GetFromJsonAsync<DriveItemPage>(next, Json, ct); }
            catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Graph(ex, VfsOperation.List, request, ct); }

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
        try { await EnsureDriveIdAsync(ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
        {
            // The change feed is a node capability, reached outside the pipeline, so there is no
            // request to name a path from.
            throw GraphErrors.Transport(ex, VfsOperation.Sync, null, null, ct);
        }

        var changes   = new List<SharePointChange>();
        var newCursor = cursor ?? "";
        var pages     = 0;
        await foreach (var page in EnumerateDeltaPagesAsync(cursor, ct))
        {
            changes.AddRange(page.Changes);
            if (page.IsComplete && page.Continuation is not null) newCursor = page.Continuation;
            WarnIfUnplaced(page.Drops, ++pages);
        }
        return new SharePointChangeBatch(changes, newCursor);
    }

    // Walk the delta feed one page at a time from startLink (null = a fresh full delta). Each page
    // carries its changes plus a Continuation link and whether it's the terminal page: for a non-final
    // page Continuation is the nextLink (resume/keep paging here); for the final page it's the deltaLink
    // (the cursor for the next incremental sync). Streaming (rather than collecting everything first)
    // lets the seeder apply, checkpoint, and report progress page by page - a large first delta isn't a
    // silent wait, and a crash resumes from the last saved Continuation instead of restarting.
    //
    // Received and Drops travel with each page because ToChange turns items away silently otherwise: a
    // page whose every item was dropped reads exactly like a quiet one, and "0 changes applied" on a busy
    // drive was indistinguishable from nothing having happened.
    private async IAsyncEnumerable<(IReadOnlyList<SharePointChange> Changes, string? Continuation, bool IsComplete, int Received, DeltaDrops Drops)>
        EnumerateDeltaPagesAsync(string? startLink, [EnumeratorCancellation] CancellationToken ct)
    {
        var next = startLink ?? $"drives/{_driveId}/root/delta";
        while (next is not null)
        {
            // Fetched inside the try, yielded outside it - a yield return cannot sit in a try that
            // has a catch. A page that fails stops the delta where it is, which is what must happen:
            // the cursor is only checkpointed for a page that actually applied.
            DriveItemPage? page;
            try { page = await _http.GetFromJsonAsync<DriveItemPage>(next, Json, ct); }
            catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
            {
                throw GraphErrors.Transport(ex, VfsOperation.Sync, null, null, ct);
            }

            var changes = new List<SharePointChange>();
            var drops   = new DeltaDrops();
            if (page?.Value is not null)
                foreach (var item in page.Value)
                    if (ToChange(item, out var drop) is { } change) changes.Add(change);
                    else drops.Add(drop, item);

            var received = page?.Value?.Count ?? 0;
            if (page?.NextLink is not null) { yield return (changes, page.NextLink, false, received, drops); next = page.NextLink; }
            else                           { yield return (changes, page?.DeltaLink, true, received, drops); next = null; }
        }
    }

    private SharePointChange? ToChange(DriveItem item, out DeltaDrop drop)
    {
        drop = DeltaDrop.None;
        if (item.Root is not null) { drop = DeltaDrop.Root; return null; }   // the drive root itself

        // Deletions are tested BEFORE anything path-derived. A tombstone is a minimal object - an id
        // and the deleted facet - so it carries no name and its parentReference has no path. Requiring
        // either first is how every deletion used to be discarded, leaving the mirror append-only.
        if (item.Deleted is not null)
        {
            if (item.Id is null) { drop = DeltaDrop.TombstoneWithoutId; return null; }   // nothing to match it on
            return new SharePointChange(MountRelPath(item), SharePointChangeType.Deleted, null, item.Id);
        }

        // Upserts carry full metadata, and a path is what an upsert is keyed on.
        if (item.Name is null) { drop = DeltaDrop.NoName; return null; }
        if (ParentRelPath(item.ParentReference?.Path) is null) { drop = DeltaDrop.NoParentPath; return null; }
        if (MountRelPath(item) is not { } mountRel) { drop = DeltaDrop.OutsideRoot; return null; }

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

    // Why ToChange turned an item away.
    private enum DeltaDrop { None, Root, OutsideRoot, TombstoneWithoutId, NoName, NoParentPath }

    // What one delta page (or a whole sync) turned away. The root arrives on every page and is not
    // worth counting. OutsideRoot is the mount working as intended - the feed is drive-wide - so it is
    // counted but not warned about. The other three are items that changed in SharePoint and never
    // reached the catalog, which is how the mirror goes stale without anything saying so.
    private sealed class DeltaDrops
    {
        private const int MaxSamples = 10;

        public int OutsideRoot, TombstoneWithoutId, NoName, NoParentPath;
        public List<string> SampleIds { get; } = [];

        public int Unplaced => TombstoneWithoutId + NoName + NoParentPath;
        public int Total    => OutsideRoot + Unplaced;

        public void Add(DeltaDrop reason, DriveItem item)
        {
            switch (reason)
            {
                case DeltaDrop.OutsideRoot:        OutsideRoot++;        return;
                case DeltaDrop.TombstoneWithoutId: TombstoneWithoutId++; break;
                case DeltaDrop.NoName:             NoName++;             break;
                case DeltaDrop.NoParentPath:       NoParentPath++;       break;
                default:                                                 return;
            }
            if (SampleIds.Count < MaxSamples) SampleIds.Add(item.Id ?? "(no id)");
        }

        public void AddTo(DeltaDrops total)
        {
            total.OutsideRoot        += OutsideRoot;
            total.TombstoneWithoutId += TombstoneWithoutId;
            total.NoName             += NoName;
            total.NoParentPath       += NoParentPath;
            foreach (var id in SampleIds)
                if (total.SampleIds.Count < MaxSamples) total.SampleIds.Add(id);
        }
    }

    private void WarnIfUnplaced(DeltaDrops drops, int page)
    {
        if (drops.Unplaced == 0) return;
        _logger?.LogWarning(
            "SharePoint delta for '{Site}': page {Page} dropped {Count} item(s) the mirror could not place "
            + "(no parent path: {NoParentPath}, no name: {NoName}, deletion without id: {NoId}). "
            + "These changes are not in the catalog. Sample item ids: {SampleIds}.",
            _sitePath ?? _driveId, page, drops.Unplaced, drops.NoParentPath, drops.NoName,
            drops.TombstoneWithoutId, string.Join(", ", drops.SampleIds));
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
    /// Rebuild the mirror from scratch (<c>IRefreshableCache</c>): drop the delta cursor, clear the
    /// catalog, and re-read the whole drive with a fresh delta. This is the answer to a catalog that
    /// has drifted from SharePoint - rows for items moved or deleted in the browser whose changes never
    /// reached it - which the incremental sync every listing runs cannot repair, because the changes
    /// that would have fixed those rows are behind the cursor.
    /// <para>
    /// The rebuild runs under the same lease as the incremental sync, and a listing waits for that
    /// lease before serving, so no instance lists from the half-filled catalog. If the rebuild is cut
    /// short it leaves a marker behind: the next sync, on any instance, resumes it from the last saved
    /// page, and until one finishes, listings throw <see cref="VfsTransientException"/> rather than
    /// serve a catalog that is missing entries.
    /// </para>
    /// </summary>
    /// <exception cref="VfsTransientException">
    /// The rebuild did not finish in this call (it resumes on the next sync), or another instance held
    /// the sync lease for every attempt.
    /// </exception>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_mirror is null) return;

        SyncOutcome outcome;
        try
        {
            await EnsureDriveIdAsync(ct);
            outcome = await RunSyncAsync(rebuild: true, ct);
        }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
        {
            // Reached as a node capability, outside the pipeline, so there is no request to name.
            throw GraphErrors.Transport(ex, VfsOperation.Sync, null, null, ct);
        }

        switch (outcome)
        {
            case SyncOutcome.Overran:
                throw new VfsTransientException(VfsFailureReason.Timeout, VfsOperation.Sync,
                    message: "The SharePoint mirror rebuild did not finish in one pass. It resumes on the next sync; "
                             + "listings fail until it completes.");
            case SyncOutcome.Exhausted:
                throw new VfsTransientException(VfsFailureReason.Unavailable, VfsOperation.Sync,
                    message: "The SharePoint mirror rebuild could not start: another instance held the sync lease.");
        }
    }

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

    // Sync-state keys. "rebuilding" is present from the moment a rebuild clears the catalog until a
    // delta runs to completion after it - the catalog is incomplete for exactly that long.
    private const string CursorKey     = "cursor";
    private const string LeaseKey      = "sync-expires";
    private const string RebuildingKey = "rebuilding";

    private enum SyncOutcome { Completed, Overran, PeerFinished, Exhausted }

    // The listing path: bring the mirror up to date, then refuse to serve it if a rebuild is still
    // outstanding. Throwing transient here is what keeps a caller that compares listings against
    // something else - a folder sync - from reading a half-filled catalog as mass deletion.
    private async Task SyncAsync(CancellationToken ct)
    {
        await RunSyncAsync(rebuild: false, ct);

        if (await _mirror!.GetStateAsync(RebuildingKey, ct) is not null)
            throw new VfsTransientException(VfsFailureReason.Unavailable, VfsOperation.Sync,
                message: "The SharePoint mirror is being rebuilt and is incomplete until the rebuild finishes.");
    }

    // Delta from the stored cursor, applied one page at a time (bulk upsert/remove = one document write
    // per page), checkpointing the cursor each page so a crash/abort resumes from there. With
    // rebuild=true the cursor and catalog are cleared first, so the delta is a full re-read of the drive.
    //
    // A datetime lease in `sync-expires` gates it across instances: acquire atomically (SetIfNull), re-up
    // the expiry each page, and abort just before the expiry if a page overruns; on success clear the
    // lease so waiters serve at once. A loser waits for the holder to finish (cleared) or die (expired),
    // taking over in the second case. An incremental sync is satisfied by a peer that finished; a rebuild
    // is not, and goes back round to take the lease itself - bounded by SyncMaxAttempts either way.
    private async Task<SyncOutcome> RunSyncAsync(bool rebuild, CancellationToken ct)
    {
        var mirror = _mirror!;
        var site   = _sitePath ?? _driveId;

        for (var attempt = 0; attempt < SyncMaxAttempts; attempt++)
        {
            var expiresAt = DateTimeOffset.UtcNow + SyncTtl;
            if (await mirror.SetIfNullStateAsync(LeaseKey, ExpiresValue(expiresAt), ct))
            {
                try { return await SyncHoldingLeaseAsync(); }
                catch
                {
                    // A failed page ends this sync for certain, so hand the lease back now rather than
                    // make every waiter sit out its expiry - but only if it is still ours to hand back.
                    if (await mirror.GetStateAsync(LeaseKey, CancellationToken.None) == ExpiresValue(expiresAt))
                        await mirror.ClearStateAsync(LeaseKey, CancellationToken.None);
                    throw;
                }
            }

            // Runs with the lease held. A local function so it renews the loop's expiresAt in place,
            // which is the value the catch above compares against.
            async Task<SyncOutcome> SyncHoldingLeaseAsync()
            {
                if (rebuild)
                {
                    // Marker first: if anything below is interrupted, the next sync must know the
                    // catalog is not whole.
                    await mirror.SetStateAsync(RebuildingKey, "1", ct);
                    await mirror.ClearStateAsync(CursorKey, ct);
                    await mirror.ClearAsync(ct);
                    _logger?.LogWarning("SharePoint mirror for '{Site}': rebuilding - catalog cleared, re-reading the drive.", site);
                }

                // A rebuild interrupted earlier (here or on another instance) is finished by this pass:
                // its cursor is the last page it saved.
                var finishing = rebuild || await mirror.GetStateAsync(RebuildingKey, ct) is not null;
                var cursor    = await mirror.GetStateAsync(CursorKey, ct);
                int received = 0, applied = 0, pages = 0;
                var dropped  = new DeltaDrops();

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
                    if (page.Continuation is not null) await mirror.SetStateAsync(CursorKey, page.Continuation, ct);

                    received += page.Received;
                    applied  += page.Changes.Count;
                    page.Drops.AddTo(dropped);
                    pages++;
                    _logger?.LogDebug(
                        "SharePoint delta for '{Site}': page {Page}, {Received} item(s) received, {Applied} applied, "
                        + "{Dropped} dropped so far{Done}.",
                        site, pages, received, applied, dropped.Total, page.IsComplete ? " (complete)" : "");
                    WarnIfUnplaced(page.Drops, pages);

                    if (DateTimeOffset.UtcNow >= expiresAt - SyncAbortMargin)   // overran → abort, let the lease lapse
                    {
                        if (finishing)
                            _logger?.LogWarning(
                                "SharePoint mirror for '{Site}': rebuild paused after {Pages} page(s); it resumes on the next sync.",
                                site, pages);
                        return SyncOutcome.Overran;
                    }
                    expiresAt = DateTimeOffset.UtcNow + SyncTtl;
                    await mirror.SetStateAsync(LeaseKey, ExpiresValue(expiresAt), ct);   // re-up
                }

                if (finishing)
                {
                    await mirror.ClearStateAsync(RebuildingKey, ct);
                    _logger?.LogWarning(
                        "SharePoint mirror for '{Site}': rebuild complete - {Applied} item(s) applied from {Received} received "
                        + "({Unplaced} could not be placed, {OutsideRoot} outside the mount root).",
                        site, applied, received, dropped.Unplaced, dropped.OutsideRoot);
                }

                await mirror.ClearStateAsync(LeaseKey, CancellationToken.None);   // done → waiters serve at once
                return SyncOutcome.Completed;
            }

            // Another instance holds the lease: wait for it to finish (cleared) or die (expired).
            while (true)
            {
                await Task.Delay(SyncPollInterval, ct);
                var v = await mirror.GetStateAsync(LeaseKey, ct);
                if (v is null)
                {
                    if (!rebuild) return SyncOutcome.PeerFinished;                         // finished → serve current mirror
                    break;                                                                 // a peer's sync is not our rebuild → take the lease
                }
                if (PastGrace(v, SyncTakeoverGrace)) { await mirror.ClearStateAsync(LeaseKey, ct); break; }   // died → take over
            }
        }
        return SyncOutcome.Exhausted;   // incremental: serve whatever is mirrored; rebuild: the caller reports it
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

    // -- Failure seams ---------------------------------------------------------
    //
    // Layer 1 of the exception contract. Graph fails in two shapes and each has its own seam:
    // EnsureOkAsync for a response that said no, Graph(...) for a call that never got one.

    // A response that arrived and refused. Stands in for EnsureSuccessStatusCode() everywhere, so
    // an HttpRequestException is never born and the status is classified while it still means
    // something. The two path strings are only built on the failure branch.
    private static async Task EnsureOkAsync(
        HttpResponseMessage resp, VfsOperation op, VfsNodeRequest request, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        throw await GraphErrors.FromResponseAsync(
            resp, op, VfsFailure.FullPath(request), VfsFailure.MountOf(request), ct);
    }

    // A call that never got an answer - dead socket, DNS, TLS, an HttpClient timeout - or one a
    // helper like GetFromJsonAsync already turned into an HttpRequestException.
    private static VfsException Graph(Exception ex, VfsOperation op, VfsNodeRequest request, CancellationToken ct)
        => GraphErrors.Transport(ex, op, VfsFailure.FullPath(request), VfsFailure.MountOf(request), ct);

    // -- Mirror seam -----------------------------------------------------------
    //
    // Every mirror call inside an operation goes through here so a catalog failure is reported as
    // one. Without it the node's own catch would file a catalog outage as a Graph outage, and for a
    // caching mount that is exactly backwards: the drive is still reachable, only the cache is not.
    // That bit is what a degradation policy would have to read, so it has to be right even though
    // nothing degrades yet.
    private Task MirrorAsync(Func<NodeCatalog, Task> call, VfsOperation op, VfsNodeRequest request, CancellationToken ct)
        => MirrorAsync(call, op, VfsFailure.FullPath(request), VfsFailure.MountOf(request), ct);

    private async Task MirrorAsync(
        Func<NodeCatalog, Task> call, VfsOperation op, string? path, string? mount, CancellationToken ct)
    {
        if (_mirror is null) return;
        try { await call(_mirror).ConfigureAwait(false); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
        {
            throw VfsFailure.Wrap(ex, op, path, mount, VfsFailureOrigin.Catalog);
        }
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
