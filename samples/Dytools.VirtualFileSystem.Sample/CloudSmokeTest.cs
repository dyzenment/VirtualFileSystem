using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.S3;
using Dytools.VirtualFileSystem.Nodes.Azure;
using Dytools.VirtualFileSystem.Nodes.SharePoint;

namespace Dytools.VirtualFileSystem.Sample;

// Opt-in real-world smoke test for the cloud provider packages. Runs a full
// write -> exists -> read -> info -> list -> copy -> (append) -> delete roundtrip
// through IVirtualFileSystem against a LIVE S3 bucket, Azure Blob container, or SharePoint drive.
//
//   dotnet run --project samples/Dytools.VirtualFileSystem.Sample -- s3
//   dotnet run --project samples/Dytools.VirtualFileSystem.Sample -- azure
//   dotnet run --project samples/Dytools.VirtualFileSystem.Sample -- sharepoint
//
// Configuration is read from environment variables (preferred) or prompted
// interactively. Nothing is hard-coded and no credentials are stored.
//
// S3 env vars:    VFS_S3_BUCKET (required), VFS_S3_PREFIX, VFS_S3_SERVICE_URL
//                 (set for MinIO/LocalStack), VFS_S3_REGION, VFS_S3_ACCESS_KEY,
//                 VFS_S3_SECRET_KEY (omit keys to use the AWS default credential chain).
// Azure env vars: VFS_AZURE_CONNECTION_STRING (required; 'UseDevelopmentStorage=true'
//                 for Azurite), VFS_AZURE_CONTAINER (required), VFS_AZURE_PREFIX.
// SharePoint:     VFS_SP_TOKEN (required; a current Graph bearer token), VFS_SP_DRIVE_ID
//                 (required; from /sites/{id}/drives or /me/drive), VFS_SP_PREFIX (optional folder).
internal static class CloudSmokeTest
{
    public static async Task RunAsync(string provider)
    {
        try
        {
            switch (provider.ToLowerInvariant())
            {
                case "s3":         await RunS3Async();         break;
                case "azure":      await RunAzureAsync();      break;
                case "sharepoint": await RunSharePointAsync(); break;
                default:           Console.WriteLine($"Unknown provider '{provider}'. Use 's3', 'azure', or 'sharepoint'."); break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"  ✗ FAILED: {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunS3Async()
    {
        Console.WriteLine("-- S3 smoke test --");
        var bucket     = Ask("Bucket name", "VFS_S3_BUCKET");
        var prefix     = AskOptional("Key prefix (optional)", "VFS_S3_PREFIX");
        var serviceUrl = AskOptional("Service URL (blank for real AWS; set for MinIO/LocalStack)", "VFS_S3_SERVICE_URL");
        var region     = AskOptional("Region (e.g. us-east-1; blank if using Service URL)", "VFS_S3_REGION");
        var accessKey  = AskOptional("Access key (blank to use the AWS default credential chain)", "VFS_S3_ACCESS_KEY");
        var secretKey  = AskOptional("Secret key", "VFS_S3_SECRET_KEY");

        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            config.ServiceURL     = serviceUrl;
            config.ForcePathStyle = true;   // required for MinIO / LocalStack
        }
        else if (!string.IsNullOrWhiteSpace(region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        IAmazonS3 client = !string.IsNullOrWhiteSpace(accessKey)
            ? new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config)
            : new AmazonS3Client(config);   // default chain: env vars, shared profile, IAM role

        var services = new ServiceCollection();
        services.AddSingleton(client);
        var location = string.IsNullOrWhiteSpace(prefix) ? bucket : $"{bucket}/{prefix}";
        services.AddVirtualFileSystem()
                .MountSingleton<S3Node>("/s3", o => o.UseS3Bucket(location));

        await RunRoundtripAsync(services, "/s3", appendSupported: false);
    }

    private static async Task RunAzureAsync()
    {
        Console.WriteLine("-- Azure Blob smoke test --");
        var connStr   = Ask("Connection string (or 'UseDevelopmentStorage=true' for Azurite)", "VFS_AZURE_CONNECTION_STRING");
        var container = Ask("Container name", "VFS_AZURE_CONTAINER");
        var prefix    = AskOptional("Path prefix (optional)", "VFS_AZURE_PREFIX");

        var blobService = new BlobServiceClient(connStr);
        // Create the container if needed so the test can run against a fresh account.
        await blobService.GetBlobContainerClient(container).CreateIfNotExistsAsync();

        var services = new ServiceCollection();
        services.AddSingleton(blobService);
        var location = string.IsNullOrWhiteSpace(prefix) ? container : $"{container}/{prefix}";
        services.AddVirtualFileSystem()
                .MountSingleton<AzureBlobNode>("/az", o => o.UseAzureBlob(location));

        await RunRoundtripAsync(services, "/az", appendSupported: true);
    }

    private static async Task RunSharePointAsync()
    {
        Console.WriteLine("-- SharePoint smoke test --");
        var token   = Ask("Graph access token", "VFS_SP_TOKEN");
        var driveId = Ask("Drive id", "VFS_SP_DRIVE_ID");
        var prefix  = AskOptional("Root folder within the drive (optional)", "VFS_SP_PREFIX");

        var services = new ServiceCollection();
        services.AddSingleton<ISharePointTokenProvider>(new StaticTokenProvider(token));
        services.AddVirtualFileSystem()
                .MountSingleton<SharePointNode>("/sp",
                    o => o.UseSharePointDrive(driveId, string.IsNullOrWhiteSpace(prefix) ? null : prefix));

        // Standard roundtrip (append is unsupported on SharePoint, like S3).
        await RunRoundtripAsync(services, "/sp", appendSupported: false);

        // Delta capability - specific to SharePoint.
        var sp = services.BuildServiceProvider();
        sp.InitializeVirtualFileSystem();
        await using var vfs = sp.GetRequiredService<IVirtualFileSystem>();

        Console.WriteLine();
        await Step("delta change feed", async () =>
        {
            var feed = vfs.GetCapability<ISharePointChangeFeed>("/sp")
                       ?? throw new Exception("ISharePointChangeFeed capability not available");
            var batch = await feed.GetChangesAsync(null);
            Console.Write($"{batch.Changes.Count} change(s), cursor {(string.IsNullOrEmpty(batch.Cursor) ? "none" : "returned")} ");
        });
        Console.WriteLine();
        Console.WriteLine("  ✓ SharePoint smoke passed.");
    }

    // Returns the token from the environment for the smoke test. A real app implements
    // ISharePointTokenProvider over its own credential system (which owns refresh).
    private sealed class StaticTokenProvider(string token) : ISharePointTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken ct = default)
            => ValueTask.FromResult(token);
    }

    // write -> exists -> read -> info -> list -> copy -> append -> delete.
    private static async Task RunRoundtripAsync(IServiceCollection services, string mount, bool appendSupported)
    {
        var sp = services.BuildServiceProvider();
        sp.InitializeVirtualFileSystem();
        await using var vfs = sp.GetRequiredService<IVirtualFileSystem>();

        var dir  = $"{mount}/vfs-smoketest";
        var file = $"{dir}/hello.txt";
        var copy = $"{dir}/hello-copy.txt";
        const string content = "Hello from Dytools.VirtualFileSystem!";

        Console.WriteLine();

        await Step("write", async () =>
        {
            await using var w = await vfs.OpenWriteAsync(file);
            await w.WriteAsync(Encoding.UTF8.GetBytes(content));
        });

        await Step("exists → true", async () => Assert(await vfs.ExistsAsync(file), "file should exist"));

        await Step("read back matches", async () =>
        {
            await using var r = await vfs.OpenReadAsync(file) ?? throw new Exception("read returned null");
            using var reader = new StreamReader(r);
            var got = await reader.ReadToEndAsync();
            Assert(got == content, $"content mismatch: '{got}'");
        });

        await Step("get info", async () =>
        {
            var info = await vfs.GetInfoAsync(file) ?? throw new Exception("info was null");
            Console.Write($"size={info.SizeBytes} ");
            if (info.Properties.TryGetValue("ETag", out var etag)) Console.Write($"etag={etag} ");
            if (info.Properties.TryGetValue("ContentType", out var cty)) Console.Write($"type={cty} ");
        });

        await Step("list dir", async () =>
        {
            var count = 0;
            await foreach (var _ in vfs.ListAsync(dir)) count++;
            Assert(count >= 1, "expected at least one entry");
            Console.Write($"{count} entr{(count == 1 ? "y" : "ies")} ");
        });

        await Step("copy", async () =>
        {
            await vfs.CopyAsync(file, copy);
            Assert(await vfs.ExistsAsync(copy), "copy should exist");
        });

        if (appendSupported)
            await Step("append", async () =>
            {
                await using var a = await vfs.OpenWriteAsync(file, VfsWriteMode.Append);
                await a.WriteAsync(Encoding.UTF8.GetBytes(" (appended)"));
            });
        else
            await Step("append rejected (expected)", async () =>
            {
                try
                {
                    await using var _ = await vfs.OpenWriteAsync(file, VfsWriteMode.Append);
                    Assert(false, "append should have thrown NotSupportedException");
                }
                catch (NotSupportedException) { /* expected for S3 */ }
            });

        await Step("delete both", async () =>
        {
            await vfs.DeleteAsync(file);
            await vfs.DeleteAsync(copy);
            Assert(!await vfs.ExistsAsync(file), "file should be gone after delete");
        });

        Console.WriteLine();
        Console.WriteLine("  ✓ All steps passed.");
    }

    // -- tiny helpers ----------------------------------------------------------

    private static async Task Step(string name, Func<Task> action)
    {
        Console.Write($"  · {name} … ");
        await action();
        Console.WriteLine("✓");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string Ask(string label, string envVar)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        Console.Write($"{label} [{envVar}]: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
            throw new Exception($"{label} is required - set {envVar} or enter a value.");
        return input.Trim();
    }

    private static string AskOptional(string label, string envVar)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        Console.Write($"{label} [{envVar}]: ");
        return Console.ReadLine()?.Trim() ?? "";
    }
}
