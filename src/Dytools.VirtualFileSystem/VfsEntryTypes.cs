using System.Collections.Immutable;

namespace Dytools.VirtualFileSystem;

// What a node returns from GetInfoAsync.
// Uses node-relative paths and node-known facts only.
// The VFS core composes this into a VfsEntryInfo for the consumer.
//
// RelativePath must reflect reality as the node sees it - including actual
// disk casing. e.g. node asked for "folder/FiLe.TxT", returns "folder/file.txt"
// because that is how the file actually exists on disk. The VFS uses this
// to build the correct canonical VFS path for the consumer.
public sealed record VfsNodeInfo
{
    // Actual relative path as it exists in storage (correct casing, resolved name).
    // VFS prefixes this with the mount point to produce the consumer-facing Path.
    // Must be a relative path (not starting with '/') - nodes work in mount-relative space only.
    private VfsPath _relativePath;
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

    public required bool   IsFile       { get; init; }
    public required bool   IsDirectory  { get; init; }
    public          bool   IsHidden     { get; init; }

    // Null when the node cannot provide the value (e.g. S3 has no CreatedAt).
    public DateTimeOffset? CreatedAt  { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public DateTimeOffset? AccessedAt { get; init; }   // often disabled; include but nullable
    public long?           SizeBytes  { get; init; }   // null for directories or unknown

    // Node-specific extras. Use VfsPropertyKeys constants for well-known keys.
    // Examples: VfsPropertyKeys.SymlinkTarget, VfsPropertyKeys.ContentId, "ETag", "ContentType".
    // string→string? for portability (matches Azure/S3 metadata); read typed values via
    // VfsPropertyExtensions (GetInt/GetBool/GetJson…). Structured values are JSON-encoded strings.
    public IReadOnlyDictionary<string, string?> Properties { get; init; }
        = ImmutableDictionary<string, string?>.Empty;
}

// What the consumer receives from IVirtualFileSystem.GetInfoAsync / ListInfoAsync.
// Composed by the VFS core from VfsNodeInfo + VfsContext.
// Path is always a full VFS path (/mount/relative/file.txt).
public sealed record VfsEntryInfo
{
    public required string Path        { get; init; }  // full VFS path, canonical casing
    public required string Name        { get; init; }  // final segment of Path
    public required bool   IsFile      { get; init; }
    public required bool   IsDirectory { get; init; }
    public          bool   IsHidden    { get; init; }

    // True when the consumer's path was rewritten by a VFS Alias() entry before
    // dispatch. This is a routing fact - no file exists at the alias path.
    // Set by VFS core; never by a node.
    public bool IsAliased { get; init; }

    // True when SymlinkMiddleware followed a node-level symlink pointer file.
    // The node stored VfsPropertyKeys.SymlinkTarget in its VfsNodeInfo.Properties
    // and the context was rerouted to the target path before reaching the node.
    // Set by VFS core; never by a node.
    // OS-level symlinks (NTFS reparse points, Unix symlinks) appear in
    // Properties[VfsPropertyKeys.PhysicalSymlink] if the node surfaces them.
    public bool IsSymlink { get; init; }

    public DateTimeOffset? CreatedAt  { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public DateTimeOffset? AccessedAt { get; init; }
    public long?           SizeBytes  { get; init; }

    // string→string? extension bag; read typed values via VfsPropertyExtensions.
    public IReadOnlyDictionary<string, string?> Properties { get; init; }
        = ImmutableDictionary<string, string?>.Empty;
}
