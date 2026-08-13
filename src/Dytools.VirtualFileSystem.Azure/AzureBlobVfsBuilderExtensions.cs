using Azure.Storage.Blobs;
using Dytools.VirtualFileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem.Nodes.Azure;

/// <summary>
/// Convenience mount helpers for Azure Blob Storage. Resolves the singleton
/// <see cref="BlobServiceClient"/> from the service provider at mount time.
/// </summary>
public static class AzureBlobVfsBuilderExtensions
{
    /// <summary>
    /// Mounts Azure Blob Storage at <paramref name="mountPoint"/>, resolving the registered
    /// <see cref="BlobServiceClient"/> from DI. <paramref name="location"/> is:
    /// <list type="bullet">
    /// <item><c>null</c> — account-wide: the first path segment selects the container, so
    /// <c>/mount/&lt;container&gt;/&lt;blob&gt;</c> addresses any container.</item>
    /// <item><c>"container"</c> — a single container.</item>
    /// <item><c>"container/path/prefix"</c> — a container rooted at a path prefix.</item>
    /// </list>
    /// Register a client first, e.g. <c>services.AddAzureClients(b =&gt; b.AddBlobServiceClient(...))</c>.
    /// </summary>
    public static IVfsBuilder MountAzureBlob(this IVfsBuilder builder, string mountPoint, string? location = null)
    {
        if (string.IsNullOrWhiteSpace(location))
            return builder.Mount(mountPoint,
                sp => new AzureBlobNode(sp.GetRequiredService<BlobServiceClient>()),   // account-wide
                MountLifetime.Singleton);

        var (container, prefix) = Split(location);
        return builder.Mount(mountPoint, sp => new AzureBlobNode(
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(container), prefix),
            MountLifetime.Singleton);   // node is stateless; BlobServiceClient is a singleton
    }

    // "container/a/b" → ("container", "a/b"); "container" → ("container", null).
    private static (string Container, string? Prefix) Split(string location)
    {
        var s = location.Trim('/');
        var i = s.IndexOf('/');
        return i < 0 ? (s, null) : (s[..i], s[(i + 1)..]);
    }
}
