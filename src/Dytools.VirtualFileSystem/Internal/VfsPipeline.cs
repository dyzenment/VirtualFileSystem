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
                static (ctx, ct) => ctx.ResolvedNode.OpenWriteAsync(
                    ctx.BuildNodeRequest(), ctx.WriteOptions ?? VfsWriteOptions.Default, ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (ctx, ct) => m.InvokeWriteAsync(ctx, next, ct); }
            _writeChain = chain;
        }

        // Delete - disposition (permanent / recycle) is carried on ctx.Operation
        {
            Func<VfsContext, CancellationToken, Task> chain =
                static (ctx, ct) => ctx.ResolvedNode.DeleteAsync(
                    ctx.BuildNodeRequest(), ctx.DeleteOptions ?? VfsDeleteOptions.Default, ct);
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
                static (ctx, ct) => ctx.ResolvedNode.ListAsync(
                    ctx.BuildNodeRequest(), ctx.ListOptions ?? VfsListOptions.Default, ct);
            for (var i = mw.Count - 1; i >= 0; i--)
            { var m = mw[i]; var next = chain; chain = (ctx, ct) => m.InvokeListAsync(ctx, next, ct); }
            _listChain = chain;
        }
    }

    // -- Execute ---------------------------------------------------------------
    //
    // Layer 2 of the exception contract. Every chain runs inside a filter that turns anything
    // unrecognised into a VfsException with the backend's own exception preserved, so no foreign
    // type reaches a consumer through IVirtualFileSystem - whether or not the node bothered to
    // classify it. A node that DOES classify throws a VfsException itself and passes straight
    // through, because VfsFailure.ShouldWrap declines what is already ours.
    //
    // The net sits here rather than in DefaultVirtualFileSystem for two reasons: VfsContext already
    // carries the mount and the full path, and it draws the right line. Path parsing and mount
    // resolution happen before a chain is ever entered, so they keep throwing
    // ArgumentException / InvalidOperationException - which is what a configuration error should be.

    public Task<Stream?> ExecuteReadAsync(VfsContext ctx, VfsReadOptions options, CancellationToken ct)
    {
        ctx.Operation = options;
        return Guard(_readChain, VfsOperation.Read, ctx, ct);
    }

    public Task<Stream> ExecuteWriteAsync(VfsContext ctx, VfsWriteOptions options, CancellationToken ct)
    {
        // Options.WriteMode is kept in step so middleware reading the packed flags still sees the mode.
        ctx.Options   = ctx.Options.WithWriteMode(options.Mode);
        ctx.Operation = options;
        return Guard(_writeChain, VfsOperation.Write, ctx, ct);
    }

    public Task ExecuteDeleteAsync(VfsContext ctx, VfsDeleteOptions options, CancellationToken ct)
    {
        ctx.Operation = options;
        return Guard(_deleteChain, VfsOperation.Delete, ctx, ct);
    }

    public Task ExecuteCopyAsync(VfsContext src, VfsContext dst, CancellationToken ct)
        => Guard(_copyChain, VfsOperation.Copy, src, dst, ct);

    public Task ExecuteMoveAsync(VfsContext src, VfsContext dst, CancellationToken ct)
        => Guard(_moveChain, VfsOperation.Move, src, dst, ct);

    public Task ExecuteRenameAsync(VfsContext ctx, string newName, CancellationToken ct)
        => Guard(_renameChain, VfsOperation.Rename, ctx, newName, ct);

    public Task<bool> ExecuteExistsAsync(VfsContext ctx, VfsMetadataOptions options, CancellationToken ct)
    {
        ctx.Operation = options;
        return Guard(_existsChain, VfsOperation.Exists, ctx, ct);
    }

    public Task<VfsNodeInfo?> ExecuteGetInfoAsync(VfsContext ctx, VfsMetadataOptions options, CancellationToken ct)
    {
        ctx.Operation = options;
        return Guard(_getInfoChain, VfsOperation.GetInfo, ctx, ct);
    }

    public IAsyncEnumerable<VfsNodeInfo> ExecuteListAsync(VfsContext ctx, VfsListOptions options, CancellationToken ct)
    {
        ctx.Operation = options;

        // Building the enumerable stays eager, exactly as it was before the net existed - middleware
        // that reroutes the context does so here, not on the caller's first MoveNextAsync.
        IAsyncEnumerable<VfsNodeInfo> source;
        try { source = _listChain(ctx, ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Wrap(ex, VfsOperation.List, ctx); }

        return GuardList(source, ctx, ct);
    }

    // -- The net ----------------------------------------------------------------
    //
    // One overload per chain shape. The chain, context and token are all passed as arguments rather
    // than captured, so guarding costs no closure - only the state machine the await already needed.

    private static async Task<T> Guard<T>(
        Func<VfsContext, CancellationToken, Task<T>> chain, VfsOperation op, VfsContext ctx, CancellationToken ct)
    {
        try { return await chain(ctx, ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Wrap(ex, op, ctx); }
    }

    private static async Task Guard(
        Func<VfsContext, CancellationToken, Task> chain, VfsOperation op, VfsContext ctx, CancellationToken ct)
    {
        try { await chain(ctx, ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Wrap(ex, op, ctx); }
    }

    // Copy and Move report the SOURCE path: it is the entry the operation is about, and it keeps
    // Path a real path a consumer can parse. Which side actually failed is in the inner exception.
    private static async Task Guard(
        Func<VfsContext, VfsContext, CancellationToken, Task> chain, VfsOperation op,
        VfsContext src, VfsContext dst, CancellationToken ct)
    {
        try { await chain(src, dst, ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Wrap(ex, op, src); }
    }

    private static async Task Guard(
        Func<VfsContext, string, CancellationToken, Task> chain, VfsOperation op,
        VfsContext ctx, string newName, CancellationToken ct)
    {
        try { await chain(ctx, newName, ct); }
        catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Wrap(ex, op, ctx); }
    }

    // A listing is lazy, so guarding only the call that builds it would cover almost nothing - the
    // backend work happens inside MoveNextAsync, one page at a time, long after that call returned.
    // So the enumerator is driven here with each step inside the filter. Nothing is buffered, and
    // entries already yielded still reach the caller before the failure does.
    private static async IAsyncEnumerable<VfsNodeInfo> GuardList(
        IAsyncEnumerable<VfsNodeInfo> source, VfsContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // yield return is not allowed inside a try that has a catch, so each step is fetched in the
        // try and yielded outside it.
        await using var e = source.GetAsyncEnumerator(ct);
        while (true)
        {
            bool moved;
            try { moved = await e.MoveNextAsync(); }
            catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct)) { throw Wrap(ex, VfsOperation.List, ctx); }

            if (!moved) yield break;
            yield return e.Current;
        }
    }

    private static VfsException Wrap(Exception ex, VfsOperation op, VfsContext ctx)
    {
        var path  = ctx.Path.ToString();
        var mount = ctx.MountPoint.ToString();
        return VfsFailure.Wrap(ex, op, path.Length > 0 ? path : null, mount.Length > 0 ? mount : null);
    }

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
            ?? throw new VfsException(
                VfsFailureReason.NotFound, VfsOperation.Copy, src.Path.ToString(), src.MountPoint.ToString(),
                $"VFS copy source not found: {src.Path}");
        await using var w = await dst.ResolvedNode.OpenWriteAsync(dst.BuildNodeRequest(), VfsWriteOptions.Default, ct);
        await r.CopyToAsync(w, ct);
    }

    private static async Task TerminalMoveAsync(VfsContext src, VfsContext dst, CancellationToken ct)
    {
        if (ReferenceEquals(src.ResolvedNode, dst.ResolvedNode))
        {
            await src.ResolvedNode.MoveAsync(src.BuildNodeRequest(), dst.BuildNodeRequest(), ct);
            return;
        }
        // Cross-node: copy then delete - no atomicity guarantee (document this). The delete is
        // explicitly permanent: the bytes now live at the destination, and leaving a copy of them in
        // a recycle bin would turn every move into something the user has to go and empty.
        await TerminalCopyAsync(src, dst, ct);
        await src.ResolvedNode.DeleteAsync(src.BuildNodeRequest(), VfsDeleteOptions.Default, ct);
    }
}
