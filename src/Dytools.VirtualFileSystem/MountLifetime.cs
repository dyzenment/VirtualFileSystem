namespace Dytools.VirtualFileSystem;

// How often a factory-mounted node is built, and against which DI scope.
public enum MountLifetime
{
    // One node for the app, built once from the root provider.
    Singleton,

    // One node per DI scope - in a web request, the request scope - so the node
    // shares that scope's services (e.g. a DbContext). Reused within the scope.
    Scoped,

    // A fresh node per operation. Its scoped dependencies still come from the caller's scope.
    Transient,
}
