namespace Dytools.VirtualFileSystem.Middleware;

// Built by IVfsBuilder.AddRewriter(Func<VfsPath,VfsPath>).
// Rewrites ctx.Path then calls ctx.Reroute() so ResolvedNode stays consistent.
// Only operates on the base path - never modifies the query string.
public sealed class PathRewriteMiddleware(Func<VfsPath, VfsPath> rewrite) : IVfsMiddleware
{
    public Task<Stream?> InvokeReadAsync(VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream?>> next, CancellationToken ct)
    {
        Apply(ctx);
        return next(ctx, ct);
    }

    public Task<Stream> InvokeWriteAsync(VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream>> next, CancellationToken ct)
    {
        Apply(ctx);
        return next(ctx, ct);
    }

    // Delete, Copy, Move, Exists, GetInfo, List - inherit default pass-through.
    // AddRewriter() is primarily for path normalisation and prefix substitution,
    // which applies most relevantly to read/write. Override the other operations
    // in a custom middleware if you need rewriting on all operations.

    private void Apply(VfsContext ctx)
    {
        var newPath = rewrite(ctx.Path);
        if (newPath != ctx.Path) ctx.Reroute(newPath);
    }
}
