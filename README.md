# Dytools.VirtualFileSystem

A stream-first, mount-based virtual filesystem for .NET - **one path tree over
every storage backend you use.**

[![ci](https://github.com/dyzenment/VirtualFileSystem/actions/workflows/ci.yml/badge.svg)](https://github.com/dyzenment/VirtualFileSystem/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Dytools.VirtualFileSystem.svg)](https://www.nuget.org/packages/Dytools.VirtualFileSystem/)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

## The problem

The moment an application stores files in more than one place - local disk, S3,
Azure Blob, SharePoint, a network share - things get messy fast. Each backend has
its own SDK, its own addressing scheme, and its own idea of what a "path" is.
That mess leaks everywhere:

- **Your database can't store a portable path.** Is `reports/q3.pdf` on S3 or on
  disk? You end up persisting a backend tag alongside every path, and branching on
  it in code forever.
- **There's no single file structure.** Moving a folder from local disk to S3
  means rewriting call sites, not moving files.
- **Windows, UNC, and Unix paths don't mix.** `C:\reports`, `\\share\reports`,
  and `/reports` all need different handling.
- **Cross-cutting rules are copy-pasted per backend.** Auth checks, audit logging,
  path rewriting, encryption - reimplemented for each SDK.

## What it does

Dytools.VirtualFileSystem puts **every storage area under a single, system-wide
path tree.** You mount each backend at a prefix, and from then on your whole
application - and your database - deals in one uniform path space:

```
/local/c   →  LocalFsNode(@"C:\")        ┐
/archive   →  S3Node(...)   (add-on)     ├- one path tree, many backends
/team      →  SharePointNode(...)        │   store "/team/reports/q3.pdf"
/mem       →  InMemoryKvNode()           ┘   in your DB - it just works
```

A path like `/team/reports/q3.pdf` is all your code and your database ever see.
Re-point `/team` at a different backend and every stored path keeps working -
nothing else changes.

**What you get:**

- **A unified structure across all storage.** Store paths in the database, move
  data between backends, and treat everything as one filesystem - the backend is
  an implementation detail behind the mount point.
- **Path styles are normalized for you.** The library accepts UNC
  (`\\share\path`), Windows and named-drive (`C:\Windows`, `azure:\reports`), and
  Unix (`/home/user`) paths. Every path is canonicalized to a clean Unix-style
  form - `C:\Windows` becomes `/c/Windows`, `azure:\reports` becomes
  `/azure/reports`, and backslashes fold to `/` - so **nodes only ever deal with
  clean Unix paths, and consumers never think about which style went in.**
- **Custom nodes - bring any backend.** Implement a small interface and mount your
  own connector (SharePoint, FTP, a REST API, a database blob store). No path or
  cross-cutting plumbing to write.
- **Composable middleware.** Security/authorization, auditing, path rewriting, and
  more layer over *every* operation as an ordered pipeline - written once, applied
  across all backends.
- **Links and aliases.** Soft links / aliases (pure path rewrites), and hard-link
  deduplication for content-addressable backends.
- **Chainable nodes.** Because a node can wrap another node, you can stack
  behavior - e.g. a compression or encryption node in front of a local-filesystem
  or S3 node - without either side knowing.

## Why not just an `IStorage` interface?

The usual first move is a single interface - `IStorage` / `IFileStore` /
`IBlobStore` - with one implementation per backend (`LocalStorage`, `S3Storage`,
`AzureStorage`), injected where needed. It's a fine pattern until you have more
than one store live at once, because it unifies the **API** but not the **address
space**: each instance is its own island, so a bare path like `reports/q3.pdf`
means nothing without also knowing *which* store it came from.

| | `IStorage`-per-backend | Dytools.VirtualFileSystem |
|---|---|---|
| **Which backend owns a path?** | Unknown from the path - you store a backend tag beside every path and branch on it | The prefix decides: `/archive/reports/q3.pdf`. Store the path alone |
| **One structure across backends** | None - each store is a separate root | One tree spanning all mounts; `/` is the whole system |
| **Move data between backends** | Rewrite call sites; stored paths may break | Re-point the mount; stored paths keep resolving |
| **Cross-backend copy/move** | Hand-rolled per pair | `vfs.CopyAsync("/archive/x", "/local/x")` bridges nodes for you |
| **Cross-cutting concerns** | Reimplemented or decorated per store | One middleware pipeline over every operation, all backends |
| **Composition** | Nest decorators manually per store | Nodes chain - wrap any node in another |
| **Path styles** (`C:\`, `\\share`, `/home`) | Each implementation handles it (or doesn't) | Normalized once at the boundary; nodes see clean Unix paths |

In one line: **`IStorage` unifies the *API*; a virtual filesystem unifies the
*address space*.** The moment you want to persist a path in a database and have it
resolve no matter where the bytes actually live, the path has to be
self-describing - which is exactly what mounting every backend under one tree
gives you.

---

## Install

```bash
dotnet add package Dytools.VirtualFileSystem
```

Targets **.NET 10**. The core package has a single dependency
(`Microsoft.Extensions.DependencyInjection.Abstractions`) and ships with two
built-in providers - **in-memory** and **local filesystem** - so you can start
without pulling in any cloud SDKs. Heavier backends (S3, Azure Blob) are shipped
as separate add-on packages so they never enter your dependency graph unless you
ask for them.

## Quickstart

```csharp
using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

var services = new ServiceCollection();

services
    .AddVirtualFileSystem()
    .Mount("/local", new LocalFsNode(Path.GetTempPath()))
    .Mount("/mem",   new InMemoryKvNode())
    .Alias("/docs",  "/mem/documents");        // pure path rewrite, applied before dispatch

var provider = services.BuildServiceProvider();
provider.InitializeVirtualFileSystem();

await using var vfs = provider.GetRequiredService<IVirtualFileSystem>();

// Write
await using (var w = await vfs.OpenWriteAsync("/mem/hello.txt"))
    await w.WriteAsync("Hello, VFS!"u8.ToArray());

// Read
await using (var r = await vfs.OpenReadAsync("/mem/hello.txt"))
using (var reader = new StreamReader(r!))
    Console.WriteLine(await reader.ReadToEndAsync());   // → Hello, VFS!

// Typed JSON sugar
await vfs.SendAsync("/mem/config.json", new { Host = "localhost", Port = 5432 });
var cfg = await vfs.RetrieveAsync<Config>("/mem/config.json");

// Metadata, listing, copy/move/delete all work across mounts
await vfs.CopyAsync("/mem/hello.txt", "/local/hello.txt");
await foreach (var path in vfs.ListAsync("/mem"))
    Console.WriteLine(path);
```

## Consumer API

Everything is driven through the injected `IVirtualFileSystem`:

| Category | Members |
|---|---|
| Streams | `OpenReadAsync`, `OpenWriteAsync` (`Create` / `CreateNew` / `Append`) |
| File ops | `CopyAsync`, `MoveAsync`, `RenameAsync`, `DeleteAsync` |
| Metadata | `ExistsAsync`, `GetInfoAsync`, `ListAsync`, `ListInfoAsync` |
| Typed sugar | `SendAsync<T>`, `RetrieveAsync<T>` (JSON over the stream) |
| Scoping | `ScopeTo(path)` - a sub-rooted view; `Mount` / `Unmount` (instance-scoped) |
| Capabilities | `GetCapability<T>(path)` - opt-in extended behaviour a node may expose |

## Concepts

### Mounts
A **node** (`IVfsNode`) is a storage backend mounted at a path prefix. Path
resolution dispatches each call to the node owning the longest matching mount
point. Nodes work in terms of streams and a compact `VfsNodeRequest`, so a
minimal node implements just read / write / delete / list / get-info.

### Aliases
Path rewrites applied **before** mount dispatch - no file exists at the alias
path, it is purely a routing rule. Registered with `.Alias()` at startup or via
`IVfsMountRegistry` at runtime. Entries surfaced through the API report
`IsAliased = true`.

### Middleware
Cross-cutting concerns compose as an ordered pipeline wrapping all operations -
auth, auditing, path rewriting, symlink following. Add your own with `.Use<T>()`,
or use the built-ins: `.AddRewriter(...)` for path rewriting and `.UseSymlinks()`
for node-level symlink following (zero overhead for nodes that don't opt in).

### Capabilities
Nodes can expose optional behaviour beyond the core contract - e.g. content-hash
deduplication / hard links via `IDeduplicatingNode`. Consumers discover it at a
path with `vfs.GetCapability<T>(path)`, which returns `null` when the owning node
doesn't implement it. The core never calls capability interfaces itself.

## Built-in providers

| Provider | Namespace | Notes |
|---|---|---|
| In-memory KV | `Dytools.VirtualFileSystem.Nodes.InMemory` | ADS, append mode, native copy/move - ideal for tests and caches |
| Local filesystem | `Dytools.VirtualFileSystem.Nodes.LocalFs` | native `File.Copy`/`File.Move` (no buffering), casing-aware, OS symlink surfacing |

Both are BCL-only and bundled into the core package.

## Add-on providers (separate packages)

Cloud backends are published as their own packages so their SDK dependencies
stay out of projects that don't use them:

| Package | Backend | Status |
|---|---|---|
| `Dytools.VirtualFileSystem.S3` | Amazon S3 (`AWSSDK.S3`) | planned |
| `Dytools.VirtualFileSystem.Azure` | Azure Blob Storage (`Azure.Storage.Blobs`) | planned |

## Writing a custom node

Derive from `VfsNodeBase` (which provides stream-fallback `Copy`/`Move`) and
implement the five core operations:

```csharp
public sealed class MyNode : VfsNodeBase
{
    public override Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default) { ... }
    public override Task<Stream>  OpenWriteAsync(VfsNodeRequest req, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default) { ... }
    public override Task          DeleteAsync(VfsNodeRequest req, CancellationToken ct = default) { ... }
    public override IAsyncEnumerable<VfsNodeInfo> ListAsync(VfsNodeRequest req, CancellationToken ct = default) { ... }
    public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default) { ... }
}
```

See [`samples/`](samples/Dytools.VirtualFileSystem.Sample/Program.cs) for a
runnable demo covering aliases, node-level symlinks, and hard-link deduplication.

## Building from source

```bash
dotnet build
dotnet test
```

Repository layout:

```
src/        Dytools.VirtualFileSystem      the published package (core + built-in providers)
samples/    Dytools.VirtualFileSystem.Sample
tests/      Dytools.VirtualFileSystem.Tests
benchmarks/ Dytools.VirtualFileSystem.Benchmarks
```

## License

Licensed under the [Apache License 2.0](LICENSE).
