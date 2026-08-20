namespace Dytools.VirtualFileSystem;

/// <summary>How often a factory-mounted node is built, and against which DI scope.</summary>
public enum MountLifetime
{
    /// <summary>One node for the app, built once from the root provider.</summary>
    Singleton,

    /// <summary>
    /// One node per DI scope - in a web request, the request scope - so the node
    /// shares that scope's services (e.g. a DbContext). Reused within the scope.
    /// </summary>
    Scoped,

    /// <summary>A fresh node per operation. Its scoped dependencies still come from the caller's scope.</summary>
    Transient,
}
