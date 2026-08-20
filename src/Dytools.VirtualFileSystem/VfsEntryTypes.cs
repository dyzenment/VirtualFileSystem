using System.Collections.Immutable;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// What a node returns from <c>GetInfoAsync</c>.
/// Uses node-relative paths and node-known facts only.
/// The VFS core composes this into a <see cref="VfsEntryInfo"/> for the consumer.
///
/// <see cref="RelativePath"/> must reflect reality as the node sees it - including actual
/// disk casing. e.g. node asked for "folder/FiLe.TxT", returns "folder/file.txt"
/// because that is how the file actually exists on disk. The VFS uses this
/// to build the correct canonical VFS path for the consumer.
/// </summary>
public sealed record VfsNodeInfo
{
    // Actual relative path as it exists in storage (correct casing, resolved name).
    // VFS prefixes this with the mount point to produce the consumer-facing Path.
    // Must be a relative path (not starting with '/') - nodes work in mount-relative space only.
    private VfsPath _relativePath;

    /// <summary>
    /// Actual relative path as it exists in storage (correct casing, resolved name).
    /// VFS prefixes this with the mount point to produce the consumer-facing Path.
    /// Must be a relative path (not starting with '/') - nodes work in mount-relative space only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The assigned value is an absolute path.</exception>
    public required VfsPath RelativePath
    {
        get => _relativePath;
        init
        {
            if (value.IsAbsolute)
                throw new InvalidOperationException(
                    $"VfsNodeInfo.RelativePath must not be absolute. " +
                    $"Nodes return mount-relative paths only. Got: '{value}'");
            _relativePath = value;
        }
    }

    /// <summary>True when this entry is a file.</summary>
    public required bool   IsFile       { get; init; }
    /// <summary>True when this entry is a directory.</summary>
    public required bool   IsDirectory  { get; init; }
    /// <summary>True when the entry is flagged hidden by the backend.</summary>
    public          bool   IsHidden     { get; init; }

    /// <summary>Creation time, or null when the node cannot provide it (e.g. S3 has no CreatedAt).</summary>
    public DateTimeOffset? CreatedAt  { get; init; }
    /// <summary>Last-modified time, or null when the node cannot provide it.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }
    /// <summary>Last-accessed time, or null when the node cannot provide it (often disabled).</summary>
    public DateTimeOffset? AccessedAt { get; init; }
    /// <summary>Size in bytes, or null for directories or when unknown.</summary>
    public long?           SizeBytes  { get; init; }

    /// <summary>
    /// Node-specific extras. Use <c>VfsPropertyKeys</c> constants for well-known keys.
    /// Examples: <c>VfsPropertyKeys.SymlinkTarget</c>, <c>VfsPropertyKeys.ContentId</c>, "ETag", "ContentType".
    /// string→string? for portability (matches Azure/S3 metadata); read typed values via
    /// <c>VfsPropertyExtensions</c> (GetInt/GetBool/GetJson…). Structured values are JSON-encoded strings.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Properties { get; init; }
        = ImmutableDictionary<string, string?>.Empty;
}

/// <summary>
/// What the consumer receives from <c>IVirtualFileSystem.GetInfoAsync</c> / <c>ListInfoAsync</c>.
/// Composed by the VFS core from <see cref="VfsNodeInfo"/> + <see cref="VfsContext"/>.
/// Path is always a full VFS path (/mount/relative/file.txt).
/// </summary>
public sealed record VfsEntryInfo
{
    /// <summary>Full VFS path, canonical casing.</summary>
    public required string Path        { get; init; }
    /// <summary>Final segment of <see cref="Path"/>.</summary>
    public required string Name        { get; init; }
    /// <summary>True when this entry is a file.</summary>
    public required bool   IsFile      { get; init; }
    /// <summary>True when this entry is a directory.</summary>
    public required bool   IsDirectory { get; init; }
    /// <summary>True when the entry is flagged hidden by the backend.</summary>
    public          bool   IsHidden    { get; init; }

    /// <summary>
    /// True when the consumer's path was rewritten by a VFS <c>Alias()</c> entry before
    /// dispatch. This is a routing fact - no file exists at the alias path.
    /// Set by VFS core; never by a node.
    /// </summary>
    public bool IsAliased { get; init; }

    /// <summary>
    /// True when <c>SymlinkMiddleware</c> followed a node-level symlink pointer file.
    /// The node stored <c>VfsPropertyKeys.SymlinkTarget</c> in its <see cref="VfsNodeInfo.Properties"/>
    /// and the context was rerouted to the target path before reaching the node.
    /// Set by VFS core; never by a node.
    /// OS-level symlinks (NTFS reparse points, Unix symlinks) appear in
    /// <c>Properties[VfsPropertyKeys.PhysicalSymlink]</c> if the node surfaces them.
    /// </summary>
    public bool IsSymlink { get; init; }

    /// <summary>Creation time, or null when unavailable.</summary>
    public DateTimeOffset? CreatedAt  { get; init; }
    /// <summary>Last-modified time, or null when unavailable.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }
    /// <summary>Last-accessed time, or null when unavailable.</summary>
    public DateTimeOffset? AccessedAt { get; init; }
    /// <summary>Size in bytes, or null for directories or when unknown.</summary>
    public long?           SizeBytes  { get; init; }

    /// <summary>string→string? extension bag; read typed values via <c>VfsPropertyExtensions</c>.</summary>
    public IReadOnlyDictionary<string, string?> Properties { get; init; }
        = ImmutableDictionary<string, string?>.Empty;
}
