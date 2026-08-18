# Dytools.VirtualFileSystem.S3

Amazon S3 provider for [Dytools.VirtualFileSystem](https://www.nuget.org/packages/Dytools.VirtualFileSystem/).

Mount an S3 bucket under a path in the virtual filesystem and read, write, list,
copy, and delete objects through the same unified API as every other backend.

```bash
dotnet add package Dytools.VirtualFileSystem.S3
```

## Usage

Register an `IAmazonS3` client (the AWS SDK recommends a singleton), then mount:

```csharp
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.S3;

services.AddAWSService<IAmazonS3>();   // from AWSSDK.Extensions.NETCore.Setup

services
    .AddVirtualFileSystem()
    .MountSingleton<S3Node>("/archive", o => o.UseS3Bucket("my-bucket"))                // whole bucket
    .MountSingleton<S3Node>("/reports", o => o.UseS3Bucket("my-bucket/reports/2026"));  // rooted at a key prefix
```

`UseS3Bucket` takes `"bucket"` or `"bucket/key/prefix"` - the first segment is the
bucket, the rest an optional key prefix. The `S3Node` resolves the registered
`IAmazonS3` from DI. To pass a client explicitly, use the factory overload:

```csharp
.Mount("/archive", sp => new S3Node(sp.GetRequiredService<IAmazonS3>(), "my-bucket"),
       MountLifetime.Singleton)
```

## Caching catalog (optional)

For faster, cheaper repeated listings, mirror the bucket's structure into an `IVfsCatalog`
with `UseS3CachingCatalog()`:

```csharp
services.AddVfsJsonCatalog(sp => sp.NodeAt("/dev/catalog"));   // or a database-backed catalog for scale

services.AddVirtualFileSystem()
    .MountSingleton<S3Node>("/archive", o => o.UseS3Bucket("my-bucket").UseS3CachingCatalog());
```

S3 has no cheap delta, so the mirror is **seeded once** (one full listing), then served locally -
listings (including recursive ones) skip the network, cutting latency and `LIST` cost. Changes
made **through this VFS** are written through immediately (write/delete/copy/move). Changes made
**outside** it aren't seen until you re-sync: call `RefreshAsync` on demand -

```csharp
await vfs.GetCapability<ICatalogMirror>("/archive")!.RefreshAsync();
```

Select a keyed or partitioned catalog with `UseS3CachingCatalog(partition: …, serviceKey: …)`. Use a
database-backed `IVfsCatalog` for large buckets - the built-in JSON catalog rewrites its whole
file per change and is meant for small/medium namespaces.

## Notes

- Credentials and region are configured on the `IAmazonS3` client - this package
  never handles raw credentials.
- `CopyAsync` / `MoveAsync` use server-side S3 `CopyObject` (no bytes through the client).
- `Append` write mode throws `NotSupportedException` - S3 objects are immutable.
- Object metadata surfaces `ETag` and `ContentType` in `VfsNodeInfo.Properties`.

Licensed under the Apache License 2.0.
