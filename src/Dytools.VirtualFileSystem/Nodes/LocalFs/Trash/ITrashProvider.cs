using System.Runtime.InteropServices;

namespace Dytools.VirtualFileSystem.Nodes.LocalFs.Trash;

/// <summary>
/// The OS recycle bin, behind one method.
/// </summary>
/// <remarks>
/// There is deliberately no "path to the recycle bin" on this interface, because on two of the three
/// platforms no such path is usable:
/// <list type="bullet">
/// <item><description>
/// <b>Windows</b> - <c>C:\$Recycle.Bin\&lt;SID&gt;</c> is real, but moving a file into it does nothing.
/// The shell needs a paired <c>$I</c> metadata file recording the original path and deletion time
/// alongside the <c>$R</c> payload, and the volume's bin index updated; a hand-rolled move produces an
/// invisible orphan with no Restore. <c>CSIDL_BITBUCKET</c> hands back a virtual shell folder, not a
/// filesystem path.
/// </description></item>
/// <item><description>
/// <b>macOS</b> - <c>~/.Trash</c> and <c>/Volumes/&lt;vol&gt;/.Trashes/&lt;uid&gt;</c> are real, but a
/// manual move loses the extended attribute Finder's "Put Back" reads.
/// </description></item>
/// <item><description>
/// <b>Linux</b> - here the path <em>is</em> the API. The freedesktop.org trash specification is a
/// filesystem layout you implement yourself, which is what <see cref="FreeDesktopTrashProvider"/> does.
/// </description></item>
/// </list>
/// </remarks>
internal interface ITrashProvider
{
    /// <summary>Whether this platform has a bin at all. False only on <see cref="UnsupportedTrashProvider"/>.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Whether <paramref name="physicalPath"/> sits somewhere with a usable bin. A cheap check against
    /// the volume - it does not touch the entry, and a true answer is not a promise that
    /// <see cref="Trash"/> will succeed.
    /// </summary>
    bool CanRecycle(string physicalPath);

    /// <summary>
    /// Sends the file or directory to the bin, recoverably.
    /// </summary>
    /// <returns>
    /// Where the entry now lives, when the platform can say - macOS and Linux both can. Windows
    /// returns null: the shell does not report the <c>$R</c> name it chose, and reading it back would
    /// mean enumerating the bin through COM.
    /// </returns>
    /// <exception cref="IOException">The OS refused the operation.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform has no bin.</exception>
    string? Trash(string physicalPath);
}

/// <summary>Resolves the <see cref="ITrashProvider"/> for the running OS, once per process.</summary>
internal static class TrashProviders
{
    /// <summary>The provider for the running OS. Stateless and thread-safe.</summary>
    public static ITrashProvider Current { get; } =
          OperatingSystem.IsWindows() ? new WindowsTrashProvider()
        : OperatingSystem.IsMacOS()   ? new MacTrashProvider()
        : OperatingSystem.IsLinux()   ? new FreeDesktopTrashProvider()
        :                               new UnsupportedTrashProvider();
}

/// <summary>Every other OS. Recycling is unavailable, which callers handle rather than crash on.</summary>
internal sealed class UnsupportedTrashProvider : ITrashProvider
{
    public bool IsSupported => false;

    public bool CanRecycle(string physicalPath) => false;

    public string? Trash(string physicalPath)
        => throw new PlatformNotSupportedException(
            $"No recycle bin implementation for this platform ({RuntimeInformation.OSDescription}).");
}
