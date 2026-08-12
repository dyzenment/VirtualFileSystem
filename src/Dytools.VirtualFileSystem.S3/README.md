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
    .MountS3("/archive", "my-bucket")                // whole bucket
    .MountS3("/reports", "my-bucket/reports/2026");  // rooted at a key prefix
```

The location is `"bucket"` or `"bucket/key/prefix"` - the first segment is the
bucket, the rest an optional key prefix. `MountS3` resolves the registered
`IAmazonS3` from DI. To pass a client explicitly:

```csharp
.Mount("/archive", sp => new S3Node(sp.GetRequiredService<IAmazonS3>(), "my-bucket"),
       MountLifetime.Singleton)
```

## Notes

- Credentials and region are configured on the `IAmazonS3` client - this package
  never handles raw credentials.
- `CopyAsync` / `MoveAsync` use server-side S3 `CopyObject` (no bytes through the client).
- `Append` write mode throws `NotSupportedException` - S3 objects are immutable.
- Object metadata surfaces `ETag` and `ContentType` in `VfsNodeInfo.Properties`.

Licensed under the Apache License 2.0.
