using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Dytools.VirtualFileSystem.Nodes.LocalFs.Trash;

/// <summary>
/// The Linux desktop trash, implementing the freedesktop.org Trash specification (v1.0) directly.
/// </summary>
/// <remarks>
/// Unlike Windows and macOS, there is no OS call to make here - the specification <em>is</em> a
/// filesystem layout, and every desktop environment (GNOME, KDE, XFCE) reads the same one. A trash
/// directory holds <c>files/</c> and <c>info/</c>; an entry is a renamed payload under <c>files/</c>
/// plus a <c>.trashinfo</c> record under <c>info/</c> giving its original path and deletion time.
/// <para>
/// Two rules in the spec drive most of the code below. The <c>.trashinfo</c> file is created first
/// and exclusively, because that is what reserves the name against another trash implementation
/// racing for it. And an entry is never copied into the home trash from another filesystem - a trash
/// on the entry's own volume is used instead, so the operation stays a rename.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class FreeDesktopTrashProvider : ITrashProvider
{
    public bool IsSupported => true;

    /// <summary>
    /// Whether a trash directory can be named for this entry - the home trash needs <c>HOME</c> or
    /// <c>XDG_DATA_HOME</c> set, a volume trash needs the numeric uid. Whether that directory can
    /// actually be created is left to <see cref="Trash"/>, since finding out means creating it.
    /// </summary>
    public bool CanRecycle(string physicalPath)
    {
        try   { return ResolveTrashDir(Path.GetFullPath(physicalPath), out _) is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    /// <summary>Recycles the entry, returning its path under the trash's <c>files/</c> directory.</summary>
    public string? Trash(string physicalPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(physicalPath));

        // A volume root has no name to give the payload under files/, and there is no sensible thing
        // to record as its original path either.
        var original = Path.GetFileName(full);
        if (original.Length == 0)
            throw new IOException($"'{physicalPath}' is a filesystem root and cannot be trashed.");

        var trashDir = ResolveTrashDir(full, out var topDir)
            ?? throw new IOException(
                $"No freedesktop.org trash directory could be resolved for '{full}' " +
                "(neither XDG_DATA_HOME nor HOME is set, and the uid could not be read).");

        var filesDir = Path.Combine(trashDir, "files");
        var infoDir  = Path.Combine(trashDir, "info");
        Directory.CreateDirectory(filesDir);
        Directory.CreateDirectory(infoDir);

        // Absolute for the home trash; relative to the volume root for a volume trash, so the entry
        // still resolves if the volume is later mounted somewhere else.
        var recorded = topDir is null ? full : Path.GetRelativePath(topDir, full);

        for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
        {
            var name     = attempt == 0 ? original : Disambiguate(original, attempt);
            var infoPath = Path.Combine(infoDir, name + ".trashinfo");
            var target   = Path.Combine(filesDir, name);

            FileStream info;
            try
            {
                // CreateNew, not Create: an exclusive create is the spec's name reservation, and the
                // only thing stopping two trash implementations from claiming the same name at once.
                info = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            // Only a name collision is retryable; a permission or disk error must surface.
            catch (IOException) when (File.Exists(infoPath)) { continue; }

            var committed = false;
            try
            {
                // A payload with no info file means someone else's trash is inconsistent. Yield the
                // name rather than clobber whatever is sitting there.
                if (Exists(target)) continue;

                using (var writer = new StreamWriter(info, Utf8NoBom) { NewLine = "\n" })
                {
                    writer.WriteLine("[Trash Info]");
                    writer.WriteLine($"Path={EncodePath(recorded)}");
                    // Local time with no offset, exactly as the spec words it.
                    writer.WriteLine("DeletionDate=" +
                        DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                }

                // Both are a rename() underneath. The trash directory was chosen to be on the same
                // filesystem, so this never degrades into a copy.
                if (Directory.Exists(full) && !IsSymlink(full)) Directory.Move(full, target);
                else                                            File.Move(full, target);

                committed = true;
                return target;
            }
            finally
            {
                if (!committed)
                {
                    info.Dispose();
                    TryDelete(infoPath);
                }
            }
        }

        throw new IOException($"Could not find a free name in '{filesDir}' for '{full}'.");
    }

    // -- Trash directory resolution --------------------------------------------

    private const int MaxNameAttempts = 10_000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The trash directory for an entry. <paramref name="topDir"/> is null for the home trash (the
    /// recorded original path is then absolute) and the volume root otherwise.
    /// </summary>
    private static string? ResolveTrashDir(string fullPath, out string? topDir)
    {
        topDir = null;

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home)) dataHome = Path.Combine(home, ".local", "share");
        }

        var entryTop = FindTopDir(fullPath);

        if (!string.IsNullOrEmpty(dataHome))
        {
            var homeTrash = Path.Combine(dataHome, "Trash");
            // Same filesystem: the home trash is the right answer and the move stays a rename.
            if (string.Equals(FindTopDir(homeTrash), entryTop, StringComparison.Ordinal))
                return homeTrash;
        }

        // Different filesystem. The spec forbids copying into the home trash, so the entry goes to a
        // trash on its own volume - which needs the uid to name.
        if (GetUid() is not { } uid) return null;
        topDir = entryTop;

        // $topdir/.Trash is the administrator-provided form. It only counts when it is a real
        // directory with the sticky bit set: without it, any user could replace another's subdirectory.
        var shared = Path.Combine(entryTop, ".Trash");
        if (Directory.Exists(shared) && !IsSymlink(shared) && IsSticky(shared))
            return Path.Combine(shared, uid.ToString(CultureInfo.InvariantCulture));

        return Path.Combine(entryTop, $".Trash-{uid.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// The mount point the path sits on: the longest entry in <c>/proc/self/mounts</c> that is a
    /// directory prefix of it.
    /// </summary>
    /// <remarks>
    /// This replaces comparing <c>st_dev</c> from <c>stat</c>, which would mean p/invoking into libc -
    /// where the symbol is versioned on glibc and the plain <c>libc</c> name does not reliably resolve
    /// under .NET's native library probing. Reading the mount table gets the same answer with no
    /// interop, and this provider is Linux-only so <c>/proc</c> is a fair assumption. Where it cannot
    /// be read (a locked-down container), everything resolves to <c>/</c> and the home trash is used -
    /// at worst a cross-device <c>File.Move</c>, which .NET degrades to a copy rather than failing.
    /// </remarks>
    private static string FindTopDir(string fullPath)
    {
        var best = "/";
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mounts"))
            {
                var fields = line.Split(' ');
                if (fields.Length < 2) continue;

                var mount = UnescapeMountField(fields[1]);
                if (mount.Length > best.Length && IsUnder(fullPath, mount)) best = mount;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to "/".
        }
        return best;
    }

    private static bool IsUnder(string path, string directory)
    {
        if (directory == "/") return true;
        if (!path.StartsWith(directory, StringComparison.Ordinal)) return false;
        return path.Length == directory.Length || path[directory.Length] == '/';
    }

    // /proc/self/mounts escapes space, tab, newline and backslash in the mount point as octal.
    private static string UnescapeMountField(string field)
    {
        if (!field.Contains('\\')) return field;

        var sb = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length
                && IsOctal(field[i + 1]) && IsOctal(field[i + 2]) && IsOctal(field[i + 3]))
            {
                sb.Append((char)(((field[i + 1] - '0') << 6) + ((field[i + 2] - '0') << 3) + (field[i + 3] - '0')));
                i += 3;
            }
            else sb.Append(field[i]);
        }
        return sb.ToString();

        static bool IsOctal(char c) => c is >= '0' and <= '7';
    }

    /// <summary>
    /// The real uid, from <c>/proc/self/status</c> - whose <c>Uid:</c> line lists real, effective,
    /// saved-set and filesystem uids, in that order.
    /// </summary>
    private static int? GetUid()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var fields = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 0 && int.TryParse(fields[0], CultureInfo.InvariantCulture, out var uid))
                    return uid;
                break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No /proc - fall through.
        }
        return null;
    }

    // -- Small helpers ---------------------------------------------------------

    // "report.pdf" + 2 -> "report.2.pdf"; ".bashrc" + 2 -> ".bashrc.2". Any unique name satisfies the
    // spec, since the original is recorded in the .trashinfo rather than inferred from this one.
    private static string Disambiguate(string name, int sequence)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0
            ? $"{name}.{sequence}"
            : $"{name[..dot]}.{sequence}{name[dot..]}";
    }

    // Percent-encode as a URL path: the spec's Path= value, with separators left intact.
    private static string EncodePath(string path)
        => Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsSymlink(string path)
    {
        try   { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsSticky(string path)
    {
        try   { return (File.GetUnixFileMode(path) & UnixFileMode.StickyBit) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
