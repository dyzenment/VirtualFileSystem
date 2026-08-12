using Amazon.S3;
using Dytools.VirtualFileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem.Nodes.S3;

/// <summary>
/// Convenience mount helpers for Amazon S3. Resolves the singleton
/// <see cref="IAmazonS3"/> from the service provider at mount time.
/// </summary>
public static class S3VfsBuilderExtensions
{
    /// <summary>
    /// Mounts an S3 bucket at <paramref name="mountPoint"/>, resolving the
    /// registered <see cref="IAmazonS3"/> client from DI. Register a client first,
    /// e.g. with <c>services.AddAWSService&lt;IAmazonS3&gt;()</c>.
    /// </summary>
    /// <summary>
    /// Mounts an S3 location given as <c>"bucket"</c> or <c>"bucket/key/prefix"</c> - the
    /// first segment is the bucket, the rest an optional key prefix the mount is rooted at.
    /// Resolves the registered <see cref="IAmazonS3"/> client from DI.
    /// </summary>
    public static IVfsBuilder MountS3(this IVfsBuilder builder, string mountPoint, string location)
    {
        var (bucket, prefix) = Split(location);
        return builder.Mount(mountPoint,
            sp => new S3Node(sp.GetRequiredService<IAmazonS3>(), bucket, prefix),
            MountLifetime.Singleton);   // node is stateless; IAmazonS3 is a singleton
    }

    // "bucket/a/b" → ("bucket", "a/b"); "bucket" → ("bucket", null).
    private static (string Bucket, string? Prefix) Split(string location)
    {
        var s = location.Trim('/');
        var i = s.IndexOf('/');
        return i < 0 ? (s, null) : (s[..i], s[(i + 1)..]);
    }
}
