namespace Dytools.VirtualFileSystem.Middleware;

/// <summary>
/// Built by IVfsBuilder.AddRewriter(Func&lt;VfsPath,VfsPath&gt;).
/// Rewrites ctx.Path then calls ctx.Reroute() so ResolvedNode stays consistent.
/// Only operates on the base path - never modifies the query string.
/// </summary>
/// <param name="rewrite">The transform applied to the context path before each read/write.</param>
public sealed class PathRewriteMiddleware(Func<VfsPath, VfsPath> rewrite) : IVfsMiddleware
{
    /// <summary>Rewrites the context path, then invokes the read pipeline.</summary>
    public Task<Stream?> InvokeReadAsync(VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream?>> next, CancellationToken ct)
    {
        Apply(ctx);
        return next(ctx, ct);
    }

    /// <summary>Rewrites the context path, then invokes the write pipeline.</summary>
    public Task<Stream> InvokeWriteAsync(VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream>> next, CancellationToken ct)
    {
        Apply(ctx);
        return next(ctx, ct);
    }

    // Delete, Copy, Move, Exists, GetInfo, List - inherit the default pass-through.
    // AddRewriter() is primarily for path normalization and prefix substitution,
    // which applies most relevantly to read/write. Override the other operations
    // in custom middleware if you need rewriting on all operations.

    private void Apply(VfsContext ctx)
    {
        var newPath = rewrite(ctx.Path);
        if (newPath != ctx.Path) ctx.Reroute(newPath);
    }
}
