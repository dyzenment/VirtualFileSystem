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
    .MountAzureBlob("/team", "docs")              // whole container
    .MountAzureBlob("/reports", "docs/reports");  // rooted at a path prefix
```

The location is `"container"` or `"container/path/prefix"` - the first segment is
the container, the rest an optional path prefix. `MountAzureBlob` resolves the
registered `BlobServiceClient` from DI. To pass a container client explicitly:

```csharp
.Mount("/team", sp => new AzureBlobNode(
    sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("docs")),
    MountLifetime.Singleton)
```

## Notes

- Credentials are configured on the `BlobServiceClient` (connection string,
  `DefaultAzureCredential`, SAS, etc.) - this package never handles raw credentials.
- `Append` write mode uses an append blob; `Create` / `CreateNew` use a block blob.
- `CopyAsync` streams through the authenticated client so it works under every auth
  mode (OAuth, managed identity, shared key, SAS).
- Blob metadata surfaces `ETag` and `ContentType` in `VfsNodeInfo.Properties`.

Licensed under the Apache License 2.0.
