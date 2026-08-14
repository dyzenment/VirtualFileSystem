using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.S3;

// Config carried on the mount options for an S3Node.
public sealed class S3Options
{
    public string  Bucket { get; set; } = "";
    public string? Prefix { get; set; }
}

public static class S3MountOptionsExtensions
{
    // Configures an S3 mount from "bucket" or "bucket/key/prefix" - the first segment is the
    // bucket, the rest an optional key prefix the mount is rooted at:
    //
    //   .MountSingleton<S3Node>("/archive", o => o.UseS3Bucket("my-bucket"))
    //   .MountSingleton<S3Node>("/reports", o => o.UseS3Bucket("my-bucket/reports/2026"))
    public static VfsMountOptions UseS3Bucket(this VfsMountOptions options, string location)
    {
        var s = location.Trim('/');
        var i = s.IndexOf('/');
        var (bucket, prefix) = i < 0 ? (s, (string?)null) : (s[..i], s[(i + 1)..]);
        return options.Set(new S3Options { Bucket = bucket, Prefix = prefix });
    }
}
