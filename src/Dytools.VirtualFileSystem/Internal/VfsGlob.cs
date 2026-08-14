using System.Text;
using System.Text.RegularExpressions;

namespace Dytools.VirtualFileSystem.Internal;

// Compiles a leaf-name glob ('*' any run, '?' any single char) into a matcher.
// The VFS owns this dialect: it is validated once (invalid syntax throws at the boundary)
// and applied uniformly, so a pattern means the same thing on every backend.
internal sealed class VfsGlob
{
    private readonly Regex? _regex;   // null = match everything

    private VfsGlob(Regex? regex) => _regex = regex;

    // Matches every name. Used when no pattern is supplied.
    public static readonly VfsGlob MatchAll = new((Regex?)null);

    // Builds a matcher for the pattern. Empty/null → MatchAll. Throws ArgumentException on
    // a malformed pattern (the caller surfaces this at the VFS boundary).
    public static VfsGlob Compile(string? pattern, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return MatchAll;

        var opts = RegexOptions.CultureInvariant
                   | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new VfsGlob(new Regex(Translate(pattern), opts));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid list search pattern '{pattern}'.", nameof(pattern), ex);
        }
    }

    public bool IsMatch(ReadOnlySpan<char> name)
        => _regex is null || _regex.IsMatch(name);

    // A pattern S3/Azure can push down as a native key prefix: a literal run followed by a
    // single trailing '*' and nothing else (e.g. "report*"). "*.pdf" and "a*b" are not.
    public static bool IsPurePrefix(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;   // no filter → the mount prefix itself
        var star = pattern.IndexOf('*');
        if (star < 0) return pattern.IndexOf('?') < 0;    // pure literal is a (whole-name) prefix
        return star == pattern.Length - 1                 // only one '*', at the very end
               && pattern.IndexOf('?') < 0;
    }

    // Glob → anchored regex. Only '*' and '?' are special; everything else is literal.
    private static string Translate(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 8).Append('^');
        foreach (var c in pattern)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.');  break;
                default:  sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.Append('$').ToString();
    }
}
