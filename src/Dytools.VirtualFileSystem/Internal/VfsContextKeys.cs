namespace Dytools.VirtualFileSystem.Internal;

// Constants for VfsContext.Items keys (internal middleware-to-middleware communication).
// All keys are prefixed with "vfs." to avoid collisions with user-defined keys.
// Node-facing cache keys live in VfsPropertyKeys (public) so external nodes can access them.
internal static class VfsContextKeys
{
    // Set by UserIdentityMiddlewareBase. Value: IUserIdentity.
    internal const string UserIdentity    = "vfs.identity";

    // Set by SymlinkMiddleware when a node-level symlink was followed.
    // Value: bool (true). Read by VirtualFileSystem.Enrich() to set IsSymlink.
    internal const string SymlinkFollowed = "vfs.symlink.followed";

    // Set by SymlinkMiddleware to track recursion depth for cycle detection.
    // Value: int.
    internal const string SymlinkDepth    = "vfs.symlink.depth";

    // Set by VfsContext.Reroute() when a VFS-level Alias() entry was followed.
    // Value: bool (true). Read by VirtualFileSystem.Enrich() to set IsAliased.
    internal const string AliasFollowed   = "vfs.alias.followed";

    // Set by VfsContext.Reroute() alongside AliasFollowed.
    // Value: string (the original pre-alias path). Read by VfsContext.Path.
    internal const string AliasOrigin     = "vfs.alias.origin";
}
