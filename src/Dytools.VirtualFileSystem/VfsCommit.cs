namespace Dytools.VirtualFileSystem;

/// <summary>
/// The synchronous half of commit-on-dispose, for nodes whose write stream only reaches the backend
/// when it is closed (SharePoint, S3, Azure append, dedupe - anything that stages locally first).
/// <para>
/// Such a stream does its real work in <see cref="IAsyncDisposable.DisposeAsync"/>, but nothing stops
/// a caller from writing <c>using</c> instead of <c>await using</c> - it compiles, with no warning,
/// and takes the synchronous <see cref="IDisposable.Dispose"/> path. That path has to block on an
/// async commit, and blocking on one from a thread with a <see cref="SynchronizationContext"/>
/// (WinForms, WPF, legacy ASP.NET) deadlocks: the commit's continuations queue back onto the very
/// thread that is blocked waiting for them, and the write hangs forever with nothing to show for it.
/// </para>
/// <para>
/// Routing the sync path through here makes <c>using</c> correct rather than fatal. It is still the
/// worse of the two - it blocks the calling thread for the whole upload and cannot be cancelled - so
/// prefer <c>await using</c>:
/// <code>
///   await using var stream = await vfs.OpenWriteAsync(path, VfsWriteMode.Create);
///   await content.CopyToAsync(stream);
/// </code>
/// </para>
/// </summary>
public static class VfsCommit
{
    /// <summary>
    /// Runs <paramref name="commit"/> to completion synchronously, without deadlocking on a captured
    /// synchronization context. Exceptions surface unwrapped, as they would from an <c>await</c>.
    /// </summary>
    /// <param name="commit">The commit to run. Invoked exactly once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commit"/> is null.</exception>
    public static void RunSync(Func<Task> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        // Nothing to post continuations back to, so blocking here is safe. Worth the check: it keeps
        // the failure stack of a commit unbroken in the common host (console, ASP.NET Core, tests).
        if (SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default)
        {
            commit().GetAwaiter().GetResult();
            return;
        }

        // A context or non-default scheduler is in play. Start the commit on the thread pool, where
        // there is no context for its continuations to capture, and block on that instead.
        Task.Run(commit).GetAwaiter().GetResult();
    }
}
