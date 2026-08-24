namespace Dytools.VirtualFileSystem;

/// <summary>
/// Options for the metadata queries - <c>GetInfoAsync</c> and <c>ExistsAsync</c>. They share a type
/// because they ask the same question; Exists is just "did the lookup find something".
///
/// null options anywhere means <see cref="Default"/>: symlinks are followed.
/// </summary>
public sealed record VfsMetadataOptions : VfsOperationOptions
{
    /// <summary>The defaults: symlinks are followed, so the answer describes the target.</summary>
    public static readonly VfsMetadataOptions Default = new();

    /// <summary>Does not follow symlinks - the answer describes the link itself.</summary>
    public static readonly VfsMetadataOptions NoFollow = new() { FollowSymlinks = false };

    /// <summary>
    /// Whether a symlink on the path is followed before the answer is produced. This is the
    /// <c>stat</c> / <c>lstat</c> distinction.
    /// <para>
    /// Following (the default) means the result describes the <em>target</em>: its name, its path, its
    /// size and timestamps. A link whose target is missing therefore looks exactly like a path that
    /// does not exist - <c>GetInfoAsync</c> returns null and <c>ExistsAsync</c> returns false, the same
    /// way <c>stat</c> fails with ENOENT on a dangling link.
    /// </para>
    /// <para>
    /// Not following describes the link itself, so a dangling link is still reported and can be told
    /// apart from an absent path. Use it to inspect, count or repair links rather than read through
    /// them - and to copy a link as a link instead of duplicating what it points at.
    /// </para>
    /// </summary>
    public bool FollowSymlinks { get; init; } = true;
}
