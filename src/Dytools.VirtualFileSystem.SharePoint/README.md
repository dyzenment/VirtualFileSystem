# Dytools.VirtualFileSystem.SharePoint

SharePoint / OneDrive provider for [Dytools.VirtualFileSystem](https://www.nuget.org/packages/Dytools.VirtualFileSystem/),
via Microsoft Graph.

Mount a document library (a Graph *drive*) under a path in the virtual filesystem and read,
write, list, and delete items through the same unified API as every other backend - plus a
**delta change feed** through the `ISharePointChangeFeed` capability, and an optional **caching
catalog** that makes listing large libraries fast. It talks to Graph over a plain `HttpClient`
(no Graph SDK) and **you supply the access token** - the node never sees your credentials.

```bash
dotnet add package Dytools.VirtualFileSystem.SharePoint
```

## Usage

Implement `ISharePointTokenProvider` to bridge your credential system (it owns acquisition,
refresh, and scopes), register it, then mount a drive by id:

```csharp
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.SharePoint;

sealed class MyTokenBridge : ISharePointTokenProvider
{
    public ValueTask<string> GetAccessTokenAsync(CancellationToken ct = default)
        => ValueTask.FromResult(/* your current Graph bearer token */);
}

services.AddSingleton<ISharePointTokenProvider, MyTokenBridge>();

services
    .AddVirtualFileSystem()
    .MountSingleton<SharePointNode>("/team",    o => o.UseSharePointDrive("b!AbC…"))                       // whole drive
    .MountSingleton<SharePointNode>("/reports", o => o.UseSharePointDrive("b!AbC…", "Shared Documents/Reports"));  // rooted at a folder
```

The token must carry the permissions the operations need (e.g. `Files.ReadWrite.All` /
`Sites.ReadWrite.All`), app-only or delegated as your system issues it.

### Don't have the drive id? Mount by site + library

The `b!…` drive id is the fiddly part of setup. To skip finding it by hand, mount by **site
address + library name** and let the node resolve it at runtime:

```csharp
.MountSingleton<SharePointNode>("/team",
    o => o.UseSharePointSite("contoso.sharepoint.com:/sites/Marketing", "Documents"))
```

The site address is Graph's `{hostname}:/{server-relative-path}` form. You can also just **paste the
site's browser URL** - `https://contoso.sharepoint.com/sites/Marketing` - and it's converted to that
form for you (a bare `host` or `host:/path` is used as-is):

```csharp
.MountSingleton<SharePointNode>("/team",
    o => o.UseSharePointSite("https://contoso.sharepoint.com/sites/Marketing", "Documents"))
```

On first use the node resolves the drive id, **caches it, and logs a Warning** with the exact
`UseSharePointDrive("b!…")` line to paste back into your config - so you get the id from a real
run, then switch to `UseSharePointDrive` to skip the two extra Graph calls on every start. If you
don't switch, it just resolves once per start. (`libraryName` is optional - omit it for the site's
default library. Add a `rootPath` third argument to root the mount at a folder.)

### Finding a drive id manually

A *drive* is a OneDrive or a SharePoint **document library**. Look its id up from Graph once and
put it in config. The easiest way is [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer)
(sign in, run the query, copy the `id`); `curl` with a bearer token or
`az rest --method GET --url "…"` work too.

**OneDrive** — the signed-in user, or a specific user:

```
GET https://graph.microsoft.com/v1.0/me/drive
GET https://graph.microsoft.com/v1.0/users/{user-id}/drive
```

**A SharePoint document library** — resolve the site from its URL, then list its libraries:

```
# 1. Site id from host + server-relative path:
GET https://graph.microsoft.com/v1.0/sites/contoso.sharepoint.com:/sites/Marketing?$select=id
#    → "id": "contoso.sharepoint.com,<guid>,<guid>"

# 2. The document libraries (drives) on that site - pick one by name:
GET https://graph.microsoft.com/v1.0/sites/{site-id}/drives?$select=id,name
#    → e.g. { "id": "b!AbC…", "name": "Documents" }
```

(or the site's default library directly: `GET /sites/{site-id}/drive`).

**A Team / Microsoft 365 group:**

```
GET https://graph.microsoft.com/v1.0/groups/{group-id}/drive
```

The `id` (a long `b!…` string) is what you pass to `UseSharePointDrive`. Reading a drive id needs
read access (`Sites.Read.All` / `Files.Read.All`) even if you only ever list it once.

### Supplying your own HttpClient

To use your own pre-authed `HttpClient` (e.g. from `IHttpClientFactory` with your own
handlers) instead of the token provider, construct the node directly:

```csharp
.Mount("/team", new SharePointNode(myGraphHttpClient, "b!AbC…"))
```

## Caching catalog (fast listings for large libraries)

SharePoint's `/children` listing is slow once a folder holds thousands of items. Opt into a
caching catalog and the node mirrors the drive's structure into an `IVfsCatalog`: a directory
listing then runs a fast **incremental delta sync** and serves from the local mirror, and a
*recursive* listing walks the mirror with no per-folder round-trips at all. Reads and mutations
still go straight to SharePoint and keep the mirror current.

```csharp
services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));   // or a database-backed IVfsCatalog for scale

services.AddVirtualFileSystem()
    .MountSingleton<SharePointNode>("/team",
        o => o.UseSharePointDrive("b!AbC…").UseSharePointCachingCatalog());
```

The first listing seeds the whole mirror (one full delta); after that each listing is just the
changes since last time. The seed streams the delta **page by page**: each page is applied with one
bulk write and its cursor checkpointed before the next page is fetched — so progress is durable (a
crash resumes from the last page instead of restarting), and with debug logging on you get a
per-page heartbeat (`SharePoint delta for '…': page N, M change(s) applied so far`) rather than a
silent wait. The cursor lives in the catalog, so the mirror stays incremental across restarts. For
libraries with hundreds of thousands of items a **database-backed** `IVfsCatalog` scales better
(per-page writes on the JSON catalog rewrite the whole document each time). Select a keyed catalog
or isolate several mounts within one shared catalog using
`UseSharePointCachingCatalog(partition: …, serviceKey: …)`:

```csharp
.MountSingleton<SharePointNode>("/hr",
    o => o.UseSharePointDrive("b!XyZ…").UseSharePointCachingCatalog(partition: "hr", serviceKey: "db"))
```

**The mirror is keyed by item id, not by path.** Each row stores the driveItem id in
`CatalogEntry.ContentId`, so a caching mount requires a catalog implementing
`IContentAddressedCatalog` (`JsonFileVfsCatalog` does) - it throws at mount time otherwise. That is
what lets the sync apply the two things a path-keyed mirror cannot: a **deletion**, which arrives as
a tombstone carrying an id and nothing else, and a **rename or move made outside the VFS**, which
Graph reports as an upsert at the new path while never mentioning the old one. Both resolve through
the id, so one item is always one row - folders included, which is how deleting a folder takes its
whole subtree with it. Because the tombstone has no path, a rooted mount cannot filter drive-wide
deletions before looking them up; an id it holds nothing for is simply a no-op.

## Delta change feed

`GetNodeCapability<ISharePointChangeFeed>(path)` exposes Graph's `/delta` directly (the caching
catalog uses this under the hood). You own the cursor: pass the one you saved, apply the changes,
then persist the returned cursor.

```csharp
var feed = vfs.GetNodeCapability<ISharePointChangeFeed>("/team");
var batch = await feed!.GetChangesAsync(savedCursor);   // savedCursor == null on the first run

foreach (var change in batch.Changes)
{
    // change.Id   is the driveItem id - stable across renames and moves, always present on a delete;
    // change.Type is Updated or Deleted;
    // change.Path is mount-relative, and NULL for a deletion (see below);
    // change.Info carries metadata for an upsert (null for a delete).
}

Persist(batch.Cursor);   // save AFTER applying, so a crash re-delivers rather than drops
```

**Match deletions on `Id`, not `Path`.** Graph reports a deletion as a *tombstone*: a minimal object
carrying the item id and a `deleted` facet, with no name and a `parentReference` that has no path.
There is nothing to build a path from, so `change.Path` is null for a delete in the normal case -
keep your own `id → path` mapping (as the caching catalog does) rather than expecting one. The same
id is what makes a rename tractable: Graph reports it as an *upsert at the new path* and never
mentions the old one, so anything keyed on paths alone silently keeps the old entry forever.

## Notes

- Credentials never enter this library - only the bearer token you return, attached at the
  transport boundary. Token acquisition, refresh, and scopes stay in your code.
- `Append` write mode throws `NotSupportedException` - items are rewritten whole. Files under
  4 MB upload in one request; larger files use a chunked upload session automatically.
- `CopyAsync` uses the base stream fallback (Graph's native copy is asynchronous); `MoveAsync`
  and `RenameAsync` use native Graph operations.
- Item names are case-insensitive. `ETag`, `ContentType`, `WebUrl`, and `ContentId` (the driveItem
  id) surface in `VfsNodeInfo.Properties`.
- Throttling (`429` / `503`) is retried with `Retry-After` backoff on reads; a throttled write
  surfaces the error for you to retry.

Licensed under the Apache License 2.0.

## Timestamps

`ModifiedAt` and `CreatedAt` come from the driveItem's `fileSystemInfo` facet - the file's own times,
the direct analogue of a local file's mtime - falling back to the service-controlled values for items
that never carried one. The service values stay reachable as `ServerModified` / `ServerCreated` in
`Properties`; they move for reasons unrelated to the bytes, such as a column edit or a retention
label.

That is also the only pair that round-trips: a timestamp requested through
`VfsWriteOptions.ModifiedAt` is written to `fileSystemInfo`. It rides free on a chunked upload, and
costs one extra PATCH on a small one.

## Content hashes

Graph reports a QuickXorHash for every item, and the delta feed carries it into the caching catalog
along with everything else - so with a catalog configured, hashes for a whole library come back in one
query rather than a request per file:

```csharp
var hashing = vfs.GetEntryCapability<IContentHashing>("/team/report.pdf");
var hash    = await hashing!.GetHashAsync(VfsHashAlgorithms.QuickXor, VfsHashBudget.Cached);
```

QuickXorHash is implemented in the core package, so a local file can produce the same value and be
compared against a SharePoint item without downloading either.
