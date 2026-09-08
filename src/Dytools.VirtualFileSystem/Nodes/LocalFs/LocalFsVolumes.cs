namespace Dytools.VirtualFileSystem.Nodes.LocalFs;

/// <summary>
/// The Windows volume tier: the translation between a mount-relative VFS path whose first segment
/// names a volume ("c/Users/mike/x.txt") and a host path ("C:\Users\mike\x.txt").
/// <para>
/// Only used when a <see cref="LocalFsNode"/> is rooted at the whole machine, and only on Windows -
/// every other platform has a single filesystem root, so its whole-machine node is simply rooted at
/// "/" and none of this runs.
/// </para>
/// </summary>
/// <remarks>
/// Deliberately pure string handling with no filesystem or OS calls, so the Windows conventions can
/// be exercised from any platform. The segment shape matches what <see cref="VfsPath"/> already
/// produces from a drive-prefixed path ("C:\x" normalises to "/c/x"), so the two agree by
/// construction rather than by coincidence.
/// </remarks>
internal static class LocalFsVolumes
{
    /// <summary>The tier segment that introduces a UNC path: "unc/server/share/..." is "\\server\share\...".</summary>
    internal const string UncSegment = "unc";

    /// <summary>
    /// Removes the Windows extended-length prefix, which a file picker can hand back for a long path
    /// and which nothing downstream understands: "\\?\C:\x" becomes "C:\x", and "\\?\UNC\srv\s\f"
    /// becomes "\\srv\s\f". Returns the input unchanged when there is no such prefix.
    /// </summary>
    internal static string StripExtendedPrefix(string path)
    {
        if (path.Length < 4) return path;
        if (path[0] != '\\' || path[1] != '\\' || (path[2] != '?' && path[2] != '.') || path[3] != '\\')
            return path;

        var rest = path[4..];
        return rest.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + rest[4..]
            : rest;
    }

    /// <summary>
    /// Mount-relative path to host path, or null when the first segment does not name a volume.
    /// A relative path with no volume segment has nowhere to live on Windows, so null is the honest
    /// answer rather than a guess.
    /// </summary>
    internal static string? ToHostPath(ReadOnlySpan<char> relative)
    {
        if (relative.IsEmpty) return null;

        var slash   = relative.IndexOf('/');
        var volume  = slash < 0 ? relative : relative[..slash];
        var rest    = slash < 0 ? [] : relative[(slash + 1)..];

        if (volume.Equals(UncSegment, StringComparison.OrdinalIgnoreCase))
        {
            // "unc/server/share/rest" -> "\\server\share\rest". The server alone is a legal prefix.
            if (rest.IsEmpty) return null;
            return @"\\" + rest.ToString().Replace('/', '\\');
        }

        // A real Windows volume is one letter. Anything else is a directory name that happened to
        // land in the volume position, which is not a place.
        if (volume.Length != 1 || !char.IsAsciiLetter(volume[0])) return null;

        var drive = $"{char.ToUpperInvariant(volume[0])}:\\";
        return rest.IsEmpty ? drive : drive + rest.ToString().Replace('/', '\\');
    }

    /// <summary>
    /// Host path to mount-relative path. Accepts a drive-rooted or UNC path, in either separator
    /// style, with or without an extended-length prefix. False when it is neither.
    /// </summary>
    internal static bool TryToRelative(string hostPath, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrEmpty(hostPath)) return false;

        var path = StripExtendedPrefix(hostPath).Replace('/', '\\');

        // UNC: "\\server\share\rest" -> "unc/server/share/rest".
        if (path.Length > 2 && path[0] == '\\' && path[1] == '\\')
        {
            var body = path[2..].Trim('\\');
            if (body.Length == 0) return false;
            relative = UncSegment + "/" + body.Replace('\\', '/');
            return true;
        }

        // Drive: "C:\rest" -> "c/rest", and bare "C:" or "C:\" -> "c".
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            var rest = path.Length > 2 ? path[2..].Trim('\\') : string.Empty;
            var drive = char.ToLowerInvariant(path[0]).ToString();
            relative = rest.Length == 0 ? drive : drive + "/" + rest.Replace('\\', '/');
            return true;
        }

        return false;
    }

    /// <summary>
    /// The volume segments present on this machine, for listing the tier root. Ready volumes only -
    /// an empty optical drive or a disconnected network drive would throw on any use.
    /// </summary>
    internal static IEnumerable<string> EnumerateVolumeSegments()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            var ready = false;
            try { ready = drive.IsReady; } catch { /* a vanishing drive is simply not listed */ }
            if (!ready) continue;

            if (TryToRelative(drive.Name, out var segment) && !segment.Contains('/'))
                yield return segment;
        }
    }
}
