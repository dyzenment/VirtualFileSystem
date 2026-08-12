namespace Dytools.VirtualFileSystem;

// Ordered pipeline - identical pattern to ASP.NET middleware.
// Each handler receives next and decides whether to call it, modify arguments,
// or short-circuit entirely.
//
// Default interface methods provide transparent pass-through for every operation.
// Implement only the operations you need to intercept.
//
// IMPORTANT: Read and Write have no defaults - every middleware must implement them.
// All other operations default to transparent pass-through.
//
// Registration order = execution order. First registered = outermost wrapper.
// UserIdentityMiddleware should always be registered first.
public interface IVfsMiddleware
{
    // -- Primary - no defaults, must be implemented ----------------------------

    Task<Stream?> InvokeReadAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream?>> next,
        CancellationToken ct);

    Task<Stream> InvokeWriteAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream>> next,
        CancellationToken ct);

    // -- Secondary - transparent pass-through by default -----------------------
    // Override when you need to intercept (logging, auditing, access control).

    Task InvokeDeleteAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task> next,
        CancellationToken ct) => next(ctx, ct);

    // Copy and Move receive both src and dst contexts so middleware can
    // inspect or rewrite both paths independently.
    Task InvokeCopyAsync(
        VfsContext src, VfsContext dst,
        Func<VfsContext, VfsContext, CancellationToken, Task> next,
        CancellationToken ct) => next(src, dst, ct);

    Task InvokeMoveAsync(
        VfsContext src, VfsContext dst,
        Func<VfsContext, VfsContext, CancellationToken, Task> next,
        CancellationToken ct) => next(src, dst, ct);

    Task InvokeRenameAsync(
        VfsContext ctx, string newName,
        Func<VfsContext, string, CancellationToken, Task> next,
        CancellationToken ct) => next(ctx, newName, ct);

    Task<bool> InvokeExistsAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<bool>> next,
        CancellationToken ct) => next(ctx, ct);

    Task<VfsNodeInfo?> InvokeGetInfoAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<VfsNodeInfo?>> next,
        CancellationToken ct) => next(ctx, ct);

    // ListAsync: the node returns VfsNodeInfo objects.
    // The VFS enriches these into VfsEntryInfo for the consumer after the pipeline.
    IAsyncEnumerable<VfsNodeInfo> InvokeListAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, IAsyncEnumerable<VfsNodeInfo>> next,
        CancellationToken ct) => next(ctx, ct);
}
