using System.Runtime.CompilerServices;

namespace Dytools.VirtualFileSystem.Internal;

// Immutable once built. Safe to share across all IVirtualFileSystem instances.
//
// The delegate chains are built once at construction time by walking the middleware
// list backwards (ASP.NET Core RequestDelegate pattern). Each Execute* call invokes
// a pre-built Func - zero per-call closure allocations.
internal sealed class VfsPipeline
{
    private readonly Func<VfsContext, CancellationToken, Task<Stream?>>                _readChain;
    private readonly Func<VfsContext, CancellationToken, Task<Stream>>                 _writeChain;
    private readonly Func<VfsContext, CancellationToken, Task>                         _deleteChain;
    private readonly Func<VfsContext, VfsContext, CancellationToken, Task>             _copyChain;
    private readonly Func<VfsContext, VfsContext, CancellationToken, Task>             _moveChain;
    private readonly Func<VfsContext, string, CancellationToken, Task>                 _renameChain;
    private readonly Func<VfsContext, CancellationToken, Task<bool>>                   _existsChain;
    private readonly Func<VfsContext, CancellationToken, Task<VfsNodeInfo?>>           _getInfoChain;
    private readonly Func<VfsContext, CancellationToken, IAsyncEnumerable<VfsNodeInfo>> _listChain;

    public VfsPipeline(IReadOnlyList<IVfsMiddleware> mw)
    {
        // -- Build each chain once - walk backwards so outermost middleware wraps inner. --

        // Read
        {
            Func<VfsContext, CancellationToken, Task<Stream?>> chain =
                static (ctx, ct) => ctx.ResolvedNode.OpenReadAsync(ctx.BuildNodeRequest(), ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (ctx, ct) => m.InvokeReadAsync(ctx, next, ct); }
            _readChain = chain;
        }

        // Write - mode is stored in ctx.Options before the chain runs
        {
            Func<VfsContext, CancellationToken, Task<Stream>> chain =
                static (ctx, ct) => ctx.ResolvedNode.OpenWriteAsync(ctx.BuildNodeRequest(), ctx.Options.WriteMode, ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (ctx, ct) => m.InvokeWriteAsync(ctx, next, ct); }
            _writeChain = chain;
        }

        // Delete
        {
            Func<VfsContext, CancellationToken, Task> chain =
                static (ctx, ct) => ctx.ResolvedNode.DeleteAsync(ctx.BuildNodeRequest(), ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (ctx, ct) => m.InvokeDeleteAsync(ctx, next, ct); }
            _deleteChain = chain;
        }

        // Copy
        {
            Func<VfsContext, VfsContext, CancellationToken, Task> chain =
                static (src, dst, ct) => TerminalCopyAsync(src, dst, ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (src, dst, ct) => m.InvokeCopyAsync(src, dst, next, ct); }
            _copyChain = chain;
        }

        // Move
        {
            Func<VfsContext, VfsContext, CancellationToken, Task> chain =
                static (src, dst, ct) => TerminalMoveAsync(src, dst, ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (src, dst, ct) => m.InvokeMoveAsync(src, dst, next, ct); }
            _moveChain = chain;
        }

        // Rename
        {
            Func<VfsContext, string, CancellationToken, Task> chain =
                static (ctx, newName, ct) => ctx.ResolvedNode.RenameAsync(ctx.BuildNodeRequest(), newName, ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (ctx, n, ct) => m.InvokeRenameAsync(ctx, n, next, ct); }
            _renameChain = chain;
        }

        // Exists
        {
            Func<VfsContext, CancellationToken, Task<bool>> chain =
                static (ctx, ct) => ctx.ResolvedNode.ExistsAsync(ctx.BuildNodeRequest(), ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (ctx, ct) => m.InvokeExistsAsync(ctx, next, ct); }
            _existsChain = chain;
        }

        // GetInfo
        {
            Func<VfsContext, CancellationToken, Task<VfsNodeInfo?>> chain =
                static (ctx, ct) => ctx.ResolvedNode.GetInfoAsync(ctx.BuildNodeRequest(), ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (ctx, ct) => m.InvokeGetInfoAsync(ctx, next, ct); }
            _getInfoChain = chain;
        }

        // List
        {
            Func<VfsContext, CancellationToken, IAsyncEnumerable<VfsNodeInfo>> chain =
                static (ctx, ct) => ctx.ResolvedNode.ListAsync(ctx.BuildNodeRequest(), ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (ctx, ct) => m.InvokeListAsync(ctx, next, ct); }
            _listChain = chain;
        }
    }

    // -- Execute ---------------------------------------------------------------

    public Task<Stream?> ExecuteReadAsync(VfsContext ctx, CancellationToken ct)
        => _readChain(ctx, ct);

    public Task<Stream> ExecuteWriteAsync(VfsContext ctx, VfsWriteMode mode, CancellationToken ct)
    {
        ctx.Options = ctx.Options.WithWriteMode(mode);
        return _writeChain(ctx, ct);
    }

    public Task ExecuteDeleteAsync(VfsContext ctx, CancellationToken ct)
        => _deleteChain(ctx, ct);

    public Task ExecuteCopyAsync(VfsContext src, VfsContext dst, CancellationToken ct)
        => _copyChain(src, dst, ct);

    public Task ExecuteMoveAsync(VfsContext src, VfsContext dst, CancellationToken ct)
        => _moveChain(src, dst, ct);

    public Task ExecuteRenameAsync(VfsContext ctx, string newName, CancellationToken ct)
        => _renameChain(ctx, newName, ct);

    public Task<bool> ExecuteExistsAsync(VfsContext ctx, CancellationToken ct)
        => _existsChain(ctx, ct);

    public Task<VfsNodeInfo?> ExecuteGetInfoAsync(VfsContext ctx, CancellationToken ct)
        => _getInfoChain(ctx, ct);

    public IAsyncEnumerable<VfsNodeInfo> ExecuteListAsync(VfsContext ctx, CancellationToken ct)
        => _listChain(ctx, ct);

    // -- Pipeline terminals for Copy and Move ----------------------------------
    // Same-node: let the node decide (may be native or stream fallback).
    // Cross-node: always streams - no node can optimise across mount boundaries.

    private static async Task TerminalCopyAsync(VfsContext src, VfsContext dst, CancellationToken ct)
    {
        if (ReferenceEquals(src.ResolvedNode, dst.ResolvedNode))
        {
            await src.ResolvedNode.CopyAsync(src.BuildNodeRequest(), dst.BuildNodeRequest(), ct);
            return;
        }
        await using var r = await src.ResolvedNode.OpenReadAsync(src.BuildNodeRequest(), ct)
            ?? throw new FileNotFoundException($"VFS copy source not found: {src.Path}");
        await using var w = await dst.ResolvedNode.OpenWriteAsync(dst.BuildNodeRequest(), VfsWriteMode.Create, ct);
        await r.CopyToAsync(w, ct);
    }

    private static async Task TerminalMoveAsync(VfsContext src, VfsContext dst, CancellationToken ct)
    {
        if (ReferenceEquals(src.ResolvedNode, dst.ResolvedNode))
        {
            await src.ResolvedNode.MoveAsync(src.BuildNodeRequest(), dst.BuildNodeRequest(), ct);
            return;
        }
        // Cross-node: copy then delete - no atomicity guarantee (document this).
        await TerminalCopyAsync(src, dst, ct);
        await src.ResolvedNode.DeleteAsync(src.BuildNodeRequest(), ct);
    }
}
