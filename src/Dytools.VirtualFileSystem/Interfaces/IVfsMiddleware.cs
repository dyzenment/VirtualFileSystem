namespace Dytools.VirtualFileSystem;

/// <summary>
/// Ordered pipeline - identical pattern to ASP.NET middleware. Each handler receives <c>next</c> and decides
/// whether to call it, modify arguments, or short-circuit entirely.
/// <para>
/// Default interface methods provide transparent pass-through for every operation. Implement only the
/// operations you need to intercept.
/// </para>
/// <para>
/// IMPORTANT: Read and Write have no defaults - every middleware must implement them. All other operations
/// default to transparent pass-through.
/// </para>
/// <para>
/// Registration order = execution order. First registered = outermost wrapper. <c>UserIdentityMiddleware</c>
/// should always be registered first.
/// </para>
/// </summary>
public interface IVfsMiddleware
{
    /// <summary>Intercepts a read operation. Has no default - every middleware must implement it.</summary>
    Task<Stream?> InvokeReadAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream?>> next,
        CancellationToken ct);

    /// <summary>Intercepts a write operation. Has no default - every middleware must implement it.</summary>
    Task<Stream> InvokeWriteAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream>> next,
        CancellationToken ct);

    /// <summary>
    /// Intercepts a delete operation. Transparent pass-through by default; override when you need to
    /// intercept (logging, auditing, access control).
    /// </summary>
    Task InvokeDeleteAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task> next,
        CancellationToken ct) => next(ctx, ct);

    /// <summary>
    /// Intercepts a copy operation. Receives both src and dst contexts so middleware can inspect or rewrite
    /// both paths independently. Transparent pass-through by default.
    /// </summary>
    Task InvokeCopyAsync(
        VfsContext src, VfsContext dst,
        Func<VfsContext, VfsContext, CancellationToken, Task> next,
        CancellationToken ct) => next(src, dst, ct);

    /// <summary>
    /// Intercepts a move operation. Receives both src and dst contexts so middleware can inspect or rewrite
    /// both paths independently. Transparent pass-through by default.
    /// </summary>
    Task InvokeMoveAsync(
        VfsContext src, VfsContext dst,
        Func<VfsContext, VfsContext, CancellationToken, Task> next,
        CancellationToken ct) => next(src, dst, ct);

    /// <summary>Intercepts a rename operation. Transparent pass-through by default.</summary>
    Task InvokeRenameAsync(
        VfsContext ctx, string newName,
        Func<VfsContext, string, CancellationToken, Task> next,
        CancellationToken ct) => next(ctx, newName, ct);

    /// <summary>Intercepts an exists check. Transparent pass-through by default.</summary>
    Task<bool> InvokeExistsAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<bool>> next,
        CancellationToken ct) => next(ctx, ct);

    /// <summary>Intercepts a metadata lookup. Transparent pass-through by default.</summary>
    Task<VfsNodeInfo?> InvokeGetInfoAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<VfsNodeInfo?>> next,
        CancellationToken ct) => next(ctx, ct);

    /// <summary>
    /// Intercepts a list operation. The node returns <see cref="VfsNodeInfo"/> objects; the VFS enriches
    /// these into <c>VfsEntryInfo</c> for the consumer after the pipeline. Transparent pass-through by default.
    /// </summary>
    IAsyncEnumerable<VfsNodeInfo> InvokeListAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, IAsyncEnumerable<VfsNodeInfo>> next,
        CancellationToken ct) => next(ctx, ct);
}
