using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dytools.VirtualFileSystem.Nodes.LocalFs.Trash;

/// <summary>
/// The Windows Recycle Bin, via the shell's <c>SHFileOperationW</c>.
/// </summary>
/// <remarks>
/// <c>IFileOperation</c> is the modern replacement and reports errors better, but it is a COM object
/// that wants an STA apartment and several hundred lines of interop. <c>SHFileOperationW</c> is one
/// call, handles files and whole trees identically, and is not deprecated for this use.
/// <para>
/// The <c>$Recycle.Bin</c> directory is deliberately never touched directly - see
/// <see cref="ITrashProvider"/> for why writing into it by hand does not work.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsTrashProvider : ITrashProvider
{
    public bool IsSupported => true;

    /// <summary>
    /// True for a local volume that keeps a bin. False for UNC paths and network drives, where the
    /// shell would permanently destroy the entry instead of recycling it.
    /// </summary>
    /// <remarks>
    /// Not consulted: the per-volume <c>NukeOnDelete</c> policy under
    /// <c>HKCU\...\Explorer\BitBucket\Volume\{guid}</c>, and the bin's size quota - an entry larger
    /// than the quota is permanently deleted even on a volume that has a bin. Both are why a
    /// successful recycle is reported as "the entry is gone", not as "the entry is in the bin".
    /// </remarks>
    public bool CanRecycle(string physicalPath)
    {
        try
        {
            var full = Path.GetFullPath(physicalPath);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;   // UNC, incl. \\?\UNC

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;

            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Recycles the entry. Always returns null: <c>SHFileOperationW</c> does not report the <c>$R</c>
    /// name the shell picked, and recovering it would mean walking the bin through COM.
    /// </summary>
    public string? Trash(string physicalPath)
    {
        // The shell resolves a relative path against the process CWD, which is not reliably what the
        // caller meant, and rejects the \\?\ long-path prefix outright.
        var full = Path.GetFullPath(physicalPath);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];

        // pFrom is a null-separated list of paths terminated by a second null, not a plain string.
        var buffer = Marshal.StringToHGlobalUni(full + '\0' + '\0');
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc  = FO_DELETE,
                pFrom  = buffer,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOCONFIRMMKDIR | FOF_NOERRORUI | FOF_SILENT,
            };

            var result = SHFileOperation(ref op);

            // These are legacy DE_* codes, not HRESULTs - do not hand them to
            // Marshal.ThrowExceptionForHR, which would decode them into unrelated nonsense.
            if (result != 0)
                throw new IOException($"Recycling '{full}' failed: {DescribeError(result)} (0x{result:X}).");

            if (op.fAnyOperationsAborted != 0)
                throw new IOException($"Recycling '{full}' was aborted before it completed.");

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // A few of the codes worth naming; the rest fall through to the raw value.
    private static string DescribeError(int code) => code switch
    {
        2       => "the file was not found",
        3       => "the path was not found",
        5       => "access was denied",
        0x71    => "cannot delete a file onto itself",
        0x74    => "the destination is a root directory and cannot be removed",
        0x78    => "the operation needs a confirmation the caller suppressed",
        0x7C    => "the path is invalid",
        0x402   => "the path could not be found",
        _       => "shell file operation error",
    };

    // -- Interop ---------------------------------------------------------------

    private const uint FO_DELETE = 0x0003;

    private const ushort FOF_SILENT         = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO      = 0x0040;   // the whole point: recycle rather than destroy
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    private const ushort FOF_NOERRORUI      = 0x0400;

    // Blittable so LibraryImport can source-generate the marshalling: the two string members are held
    // as raw pointers rather than as `string`, which the generator would refuse.
    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint   wFunc;
        public IntPtr pFrom;                    // double-null-terminated UTF-16 list
        public IntPtr pTo;
        public ushort fFlags;
        public int    fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    // DllImport rather than LibraryImport: the source generator requires AllowUnsafeBlocks on the
    // whole project, and a delete is far too cold a path to be worth that for a marshalling stub.
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCTW fileOp);
}
