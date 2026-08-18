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
        o => o.UseSharePointDrive("b!AbC…").UseCachingCatalog());
```

The first listing seeds the whole mirror (one full delta); after that each listing is just the
changes since last time. The delta cursor is stored in the catalog, so the mirror stays
incremental across restarts. For libraries with thousands of items use a **database-backed**
`IVfsCatalog` - the built-in JSON catalog rewrites its whole file on each change and is meant for
small/medium namespaces. Select a keyed catalog or isolate several mounts within one shared
catalog using `UseCatalogServiceKey` / `UseCatalogPartition` (the same as the dedupe node):

```csharp
.MountSingleton<SharePointNode>("/hr",
    o => o.UseSharePointDrive("b!XyZ…").UseCachingCatalog().UseCatalogServiceKey("db").UseCatalogPartition("hr"))
```

## Delta change feed

`GetCapability<ISharePointChangeFeed>(path)` exposes Graph's `/delta` directly (the caching
catalog uses this under the hood). You own the cursor: pass the one you saved, apply the changes,
then persist the returned cursor.

```csharp
var feed = vfs.GetCapability<ISharePointChangeFeed>("/team");
var batch = await feed!.GetChangesAsync(savedCursor);   // savedCursor == null on the first run

foreach (var change in batch.Changes)
{
    // change.Path is relative to the mount; change.Type is Updated or Deleted;
    // change.Info carries metadata for an upsert (null for a delete).
}

Persist(batch.Cursor);   // save AFTER applying, so a crash re-delivers rather than drops
```

## Notes

- Credentials never enter this library - only the bearer token you return, attached at the
  transport boundary. Token acquisition, refresh, and scopes stay in your code.
- `Append` write mode throws `NotSupportedException` - items are rewritten whole. Files under
  4 MB upload in one request; larger files use a chunked upload session automatically.
- `CopyAsync` uses the base stream fallback (Graph's native copy is asynchronous); `MoveAsync`
  and `RenameAsync` use native Graph operations.
- Item names are case-insensitive. `ETag`, `ContentType`, and `WebUrl` surface in
  `VfsNodeInfo.Properties`.
- Throttling (`429` / `503`) is retried with `Retry-After` backoff on reads; a throttled write
  surfaces the error for you to retry.

Licensed under the Apache License 2.0.
