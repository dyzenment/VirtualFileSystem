using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dytools.VirtualFileSystem.Nodes.LocalFs.Trash;

/// <summary>
/// The macOS Trash, via <c>-[NSFileManager trashItemAtURL:resultingItemURL:error:]</c> called through
/// the Objective-C runtime.
/// </summary>
/// <remarks>
/// The alternatives are worse. Moving the file to <c>~/.Trash</c> by hand loses the extended attribute
/// Finder reads for "Put Back", and gets the per-volume case
/// (<c>/Volumes/&lt;vol&gt;/.Trashes/&lt;uid&gt;</c>) wrong. Shelling out to
/// <c>osascript -e 'tell application "Finder" to delete ...'</c> needs Finder running, trips the TCC
/// automation consent prompt, and costs a couple of hundred milliseconds per file.
/// <para>
/// <c>trashItemAtURL:</c> handles volume selection, name collisions and Put Back metadata itself, so
/// this class is only responsible for getting the call across the interop boundary correctly.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacTrashProvider : ITrashProvider
{
    private const string Objc = "/usr/lib/libobjc.dylib";

    public bool IsSupported => true;

    /// <summary>
    /// Optimistic: <c>trashItemAtURL:</c> resolves the right trash directory for whichever volume the
    /// entry is on, and the cases where it cannot (a read-only volume, a network mount without a
    /// trash) are not knowable without a <c>statfs</c> round-trip that would cost more than the
    /// attempt. A failure surfaces from <see cref="Trash"/> as an <see cref="IOException"/> instead.
    /// </summary>
    public bool CanRecycle(string physicalPath) => true;

    /// <summary>Recycles the entry, returning the path it was given inside the Trash.</summary>
    public string? Trash(string physicalPath)
    {
        var full = Path.GetFullPath(physicalPath);

        // Nothing on a .NET thread drains autoreleased objects, so the NSString and NSURL below would
        // leak without a pool of our own.
        var pool      = MsgSend(MsgSend(GetClass("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        var utf8      = Marshal.StringToCoTaskMemUTF8(full);
        var slots     = Marshal.AllocHGlobal(IntPtr.Size * 2);   // [0] resulting NSURL*, [1] NSError*
        var resultSlot = slots;
        var errorSlot  = slots + IntPtr.Size;
        try
        {
            Marshal.WriteIntPtr(resultSlot, IntPtr.Zero);
            Marshal.WriteIntPtr(errorSlot,  IntPtr.Zero);

            var nsPath = MsgSend(GetClass("NSString"), Sel("stringWithUTF8String:"), utf8);
            var url    = MsgSend(GetClass("NSURL"),    Sel("fileURLWithPath:"),      nsPath);
            var fm     = MsgSend(GetClass("NSFileManager"), Sel("defaultManager"));

            // Objective-C BOOL is a signed char on both macOS 64-bit ABIs, not a four-byte int.
            // Declaring this as `bool` reads whatever happened to be in the upper bits of the return
            // register and fails nondeterministically.
            var ok = MsgSendTrash(fm, Sel("trashItemAtURL:resultingItemURL:error:"),
                                  url, resultSlot, errorSlot);

            if (ok == 0)
                throw new IOException(
                    $"Moving '{full}' to the Trash failed{Describe(Marshal.ReadIntPtr(errorSlot))}.");

            return PathOf(Marshal.ReadIntPtr(resultSlot));
        }
        finally
        {
            Marshal.FreeHGlobal(slots);
            Marshal.ZeroFreeCoTaskMemUTF8(utf8);
            MsgSend(pool, Sel("drain"));
        }
    }

    // -[NSURL path] as a managed string, or null when the runtime gave us nothing.
    private static string? PathOf(IntPtr url)
        => url == IntPtr.Zero ? null : StringOf(MsgSend(url, Sel("path")));

    // The NSError's localizedDescription, as ": reason", or empty when there is nothing to say.
    private static string Describe(IntPtr error)
    {
        if (error == IntPtr.Zero) return string.Empty;
        var text = StringOf(MsgSend(error, Sel("localizedDescription")));
        return string.IsNullOrEmpty(text) ? string.Empty : ": " + text;
    }

    // -[NSString UTF8String] as a managed string.
    private static string? StringOf(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = MsgSend(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    // -- Objective-C runtime interop -------------------------------------------
    //
    // objc_msgSend is variadic in its C declaration but must be called through a signature matching
    // the target method exactly - on arm64 the calling convention differs per prototype. Hence one
    // alias per shape rather than one general-purpose entry point.

    // DllImport rather than LibraryImport: the source generator requires AllowUnsafeBlocks on the
    // whole project, and a delete is far too cold a path to be worth that for a marshalling stub.

    [DllImport(Objc, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>Zero-argument message returning an object pointer.</summary>
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    /// <summary>One-argument message returning an object pointer.</summary>
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary><c>trashItemAtURL:resultingItemURL:error:</c> - three arguments, BOOL return.</summary>
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSendTrash(
        IntPtr receiver, IntPtr selector, IntPtr url, IntPtr resultingItemUrl, IntPtr error);
}
