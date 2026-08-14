# Dytools.VirtualFileSystem.Azure

Azure Blob Storage provider for [Dytools.VirtualFileSystem](https://www.nuget.org/packages/Dytools.VirtualFileSystem/).

Mount a blob container under a path in the virtual filesystem and read, write,
list, copy, and delete blobs through the same unified API as every other backend.

```bash
dotnet add package Dytools.VirtualFileSystem.Azure
```

## Usage

Register a `BlobServiceClient` (the Azure SDK recommends a singleton), then mount:

```csharp
using Azure.Storage.Blobs;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.Azure;

services.AddAzureClients(b =>            // from Microsoft.Extensions.Azure
    b.AddBlobServiceClient(connectionString));

services
    .AddVirtualFileSystem()
    .MountSingleton<AzureBlobNode>("/team", o => o.UseAzureBlob("docs"))              // whole container
    .MountSingleton<AzureBlobNode>("/reports", o => o.UseAzureBlob("docs/reports"));  // rooted at a path prefix
```

`UseAzureBlob` takes `"container"` or `"container/path/prefix"` - the first segment is
the container, the rest an optional path prefix (omit it entirely for account-wide
mode, where the first path segment selects the container). The `AzureBlobNode`
resolves the registered `BlobServiceClient` from DI. To pass a container client
explicitly, use the factory overload:

```csharp
.Mount("/team", sp => new AzureBlobNode(
    sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("docs")),
    MountLifetime.Singleton)
```

## Caching catalog (optional)

For faster, cheaper repeated listings, mirror the container's structure into an `IVfsCatalog`
with `UseCachingCatalog()`:

```csharp
services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));   // or a database-backed catalog for scale

services.AddVirtualFileSystem()
    .MountSingleton<AzureBlobNode>("/team", o => o.UseAzureBlob("docs").UseCachingCatalog());
```

By default the mirror is **seeded once** (one full listing), then served locally - listings
(including recursive ones) skip the network. Changes made **through this VFS** are written through
immediately; changes made **outside** it aren't seen until you re-sync:

```csharp
await vfs.GetCapability<ICatalogMirror>("/team")!.RefreshAsync();
```

Select a keyed or partitioned catalog with `UseCatalogServiceKey` / `UseCatalogPartition`, and use
a database-backed `IVfsCatalog` for large containers. (A future option will let accounts with the
Azure **Blob change feed** enabled keep the mirror fresh incrementally instead of by re-listing.)

## Notes

- Credentials are configured on the `BlobServiceClient` (connection string,
  `DefaultAzureCredential`, SAS, etc.) - this package never handles raw credentials.
- `Append` write mode uses an append blob; `Create` / `CreateNew` use a block blob.
- `CopyAsync` streams through the authenticated client so it works under every auth
  mode (OAuth, managed identity, shared key, SAS).
- Blob metadata surfaces `ETag` and `ContentType` in `VfsNodeInfo.Properties`.

Licensed under the Apache License 2.0.
