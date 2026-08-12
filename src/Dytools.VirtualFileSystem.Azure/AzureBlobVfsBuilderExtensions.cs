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
    /// Mounts a blob container at <paramref name="mountPoint"/>, resolving the
    /// registered <see cref="BlobServiceClient"/> from DI. Register a client first,
    /// e.g. with <c>services.AddAzureClients(b =&gt; b.AddBlobServiceClient(...))</c>.
    /// </summary>
    /// <summary>
    /// Mounts an Azure Blob location given as <c>"container"</c> or <c>"container/path/prefix"</c>
    /// - the first segment is the container, the rest an optional path prefix the mount is
    /// rooted at. Resolves the registered <see cref="BlobServiceClient"/> from DI.
    /// </summary>
    public static IVfsBuilder MountAzureBlob(this IVfsBuilder builder, string mountPoint, string location)
    {
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
