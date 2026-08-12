namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// Unit tests for VfsPath public surface.
/// Each region tests one logical concern; input variations drive each fact.
/// </summary>
public sealed class VfsPathTests
{
    // -- From(string) - normalisation -----------------------------------------

    [Fact]
    public void From_CleanAbsolutePath_ReturnsSamePath()
    {
        var p = VfsPath.From("/local/c/file.txt");
        Assert.Equal("/local/c/file.txt", p.ToString());
    }

    [Fact]
    public void From_Root_ReturnsSlash()
    {
        var p = VfsPath.From("/");
        Assert.Equal("/", p.ToString());
        Assert.Equal(1, p.Length);
    }

    [Fact]
    public void From_RelativePath_StaysRelative()
    {
        var p = VfsPath.From("docs/file.txt");
        Assert.Equal("docs/file.txt", p.ToString());
        Assert.True(p.IsRelative);
    }

    [Fact]
    public void From_BackslashSeparators_NormalisedToForwardSlash()
    {
        var p = VfsPath.From(@"\local\c\file.txt");
        Assert.Equal("/local/c/file.txt", p.ToString());
    }

    [Fact]
    public void From_MixedSlashes_AllNormalised()
    {
        var p = VfsPath.From(@"/local\c/sub\file.txt");
        Assert.Equal("/local/c/sub/file.txt", p.ToString());
    }

    [Fact]
    public void From_TrailingSlash_Stripped()
    {
        var p = VfsPath.From("/local/c/dir/");
        Assert.Equal("/local/c/dir", p.ToString()); // TODO: Idk about this
    }

    [Fact]
    public void From_MultipleConsecutiveSlashes_Collapsed()
    {
        var p = VfsPath.From("/local//c///file.txt");
        Assert.Equal("/local/c/file.txt", p.ToString());
    }

    [Fact]
    public void From_DotSegment_Removed()
    {
        var p = VfsPath.From("/local/./c/./file.txt");
        Assert.Equal("/local/c/file.txt", p.ToString());
    }

    [Fact]
    public void From_DotDot_TraversesUp()
    {
        var p = VfsPath.From("/local/c/sub/../file.txt");
        Assert.Equal("/local/c/file.txt", p.ToString());
    }

    [Fact]
    public void From_ChainedDotDot_ResolvesCorrectly()
    {
        var p = VfsPath.From("/a/b/c/../../file.txt");
        Assert.Equal("/a/file.txt", p.ToString());
    }

    [Fact]
    public void From_DotDotAtRoot_ClampsToRoot()
    {
        var p = VfsPath.From("/../../file.txt");
        Assert.Equal("/file.txt", p.ToString());
    }

    [Fact]
    public void From_DotDotCollapsesAllSegments_ClampsToRoot()
    {
        var p = VfsPath.From("/a/../..");
        Assert.Equal("/", p.ToString());
    }

    [Fact]
    public void From_EmptyString_BecomesRoot()
    {
        // Empty string has no leading slash - no segments, no absolute marker.
        // Normalises to "/" only because the root-clamp applies to absolute-only paths;
        // a bare empty input produces an empty (relative) VfsPath.
        // In practice, callers always pass a non-empty path; this just verifies no crash.
        var p = VfsPath.From("");
        Assert.Equal("", p.ToString());
        Assert.True(p.IsRelative);
    }

    [Fact]
    public void From_RootSlash_IsAbsolute()
    {
        var p = VfsPath.From("/");
        Assert.True(p.IsAbsolute);
        Assert.Equal("/", p.ToString());
    }

    // -- IsAbsolute / IsRelative -----------------------------------------------

    [Fact]
    public void IsAbsolute_ForLeadingSlashPath_True()
    {
        Assert.True(VfsPath.From("/local/c/file.txt").IsAbsolute);
    }

    [Fact]
    public void IsAbsolute_ForRelativePath_False()
    {
        Assert.False(VfsPath.From("docs/file.txt").IsAbsolute);
    }

    [Fact]
    public void IsRelative_ForRelativePath_True()
    {
        Assert.True(VfsPath.From("docs/file.txt").IsRelative);
    }

    [Fact]
    public void IsRelative_ForAbsolutePath_False()
    {
        Assert.False(VfsPath.From("/docs/file.txt").IsRelative);
    }

    [Fact]
    public void IsAbsolute_BackslashRelative_FalseAfterNorm()
    {
        // Backslash-only prefix is not absolute - backslash is a separator but
        // the path must start with '/' (forward or back) to count as absolute.
        // After normalisation "sub\file.txt" → "sub/file.txt" (relative).
        var p = VfsPath.From(@"sub\file.txt");
        Assert.True(p.IsRelative);
        Assert.Equal("sub/file.txt", p.ToString());
    }

    [Fact]
    public void IsAbsolute_LeadingBackslash_TreatedAsAbsolute()
    {
        // A leading backslash is a separator - normalises to absolute "/...".
        var p = VfsPath.From(@"\local\c\file.txt");
        Assert.True(p.IsAbsolute);
        Assert.Equal("/local/c/file.txt", p.ToString());
    }

    [Fact]
    public void IsAbsolute_Default_False()
    {
        Assert.False(default(VfsPath).IsAbsolute);
    }

    // -- From(string) - Unicode paths ------------------------------------------

    [Fact]
    public void From_UnicodeSegments_PreservedUnchanged()
    {
        // Non-ASCII characters pass through - VfsPath doesn't mangle content.
        var p = VfsPath.From("/local/文件/报告.pdf");
        Assert.Equal("/local/文件/报告.pdf", p.ToString());
    }

    [Fact]
    public void From_AccentedCharacters_PreservedUnchanged()
    {
        var p = VfsPath.From("/local/café/résumé.txt");
        Assert.Equal("/local/café/résumé.txt", p.ToString());
    }

    [Fact]
    public void From_Emoji_PreservedUnchanged()
    {
        var p = VfsPath.From("/local/🎉/party.txt");
        Assert.Equal("/local/🎉/party.txt", p.ToString());
    }

    // -- ADS (Alternate Data Stream) - stream component ------------------------

    [Fact]
    public void From_WithStream_PathSpanExcludesStream()
    {
        var p = VfsPath.From("/local/c/report.pdf:thumbnail");
        Assert.Equal("/local/c/report.pdf", new string(p.PathSpan));
    }

    [Fact]
    public void From_WithStream_StreamSpanHasName()
    {
        var p = VfsPath.From("/local/c/report.pdf:thumbnail");
        Assert.Equal("thumbnail", new string(p.StreamSpan));
    }

    [Fact]
    public void From_NoStream_StreamSpanIsEmpty()
    {
        var p = VfsPath.From("/local/c/report.pdf");
        Assert.True(p.StreamSpan.IsEmpty);
    }

    [Fact]
    public void From_StreamColonInMiddleSegment_NotTreatedAsStream()
    {
        // ':' is only an ADS separator in the LAST segment - a colon in an
        // earlier segment is just a character (though unusual on real filesystems).
        // VfsPath looks for ':' only after the last '/'.
        var p = VfsPath.From("/local/c:drive/file.txt");
        Assert.True(p.StreamSpan.IsEmpty);        // colon was not in the final segment
        Assert.Equal("/local/c:drive/file.txt", new string(p.PathSpan));
    }

    [Fact]
    public void GetStreamName_WithStream_ReturnsString()
    {
        var p = VfsPath.From("/local/c/report.pdf:thumbnail");
        Assert.Equal("thumbnail", p.GetStreamName());
    }

    [Fact]
    public void GetStreamName_NoStream_ReturnsNull()
    {
        var p = VfsPath.From("/local/c/report.pdf");
        Assert.Null(p.GetStreamName());
    }

    // -- Query string component ------------------------------------------------

    [Fact]
    public void From_WithQuery_PathSpanExcludesQuery()
    {
        var p = VfsPath.From("/local/c/report.pdf?width=200");
        Assert.Equal("/local/c/report.pdf", new string(p.PathSpan));
    }

    [Fact]
    public void From_WithQuery_QuerySpanHasParams()
    {
        var p = VfsPath.From("/local/c/report.pdf?width=200");
        Assert.Equal("width=200", new string(p.QuerySpan));
    }

    [Fact]
    public void From_NoQuery_QuerySpanIsEmpty()
    {
        var p = VfsPath.From("/local/c/report.pdf");
        Assert.True(p.QuerySpan.IsEmpty);
    }

    [Fact]
    public void From_QueryNotNormalised_PreservedExactly()
    {
        // Dot segments inside the query must NOT be resolved.
        var p = VfsPath.From("/file.txt?path=../other");
        Assert.Equal("path=../other", new string(p.QuerySpan));
    }

    [Fact]
    public void From_QueryWithSpecialChars_PreservedExactly()
    {
        var p = VfsPath.From("/file.txt?a=1&b=hello%20world&c=résumé");
        Assert.Equal("a=1&b=hello%20world&c=résumé", new string(p.QuerySpan));
    }

    [Fact]
    public void GetQueryString_WithQuery_ReturnsString()
    {
        var p = VfsPath.From("/local/c/report.pdf?width=200");
        Assert.Equal("width=200", p.GetQueryString());
    }

    [Fact]
    public void GetQueryString_NoQuery_ReturnsNull()
    {
        var p = VfsPath.From("/local/c/report.pdf");
        Assert.Null(p.GetQueryString());
    }

    // -- Stream + Query together -----------------------------------------------

    [Fact]
    public void From_StreamAndQuery_AllComponentsSplit()
    {
        var p = VfsPath.From("/local/c/report.pdf:thumbnail?width=200");
        Assert.Equal("/local/c/report.pdf", new string(p.PathSpan));
        Assert.Equal("thumbnail",           new string(p.StreamSpan));
        Assert.Equal("width=200",           new string(p.QuerySpan));
    }

    [Fact]
    public void ToString_StreamAndQuery_IncludesBoth()
    {
        var raw = "/local/c/report.pdf:thumbnail?width=200";
        var p   = VfsPath.From(raw);
        Assert.Equal(raw, p.ToString());
    }

    // -- Length ----------------------------------------------------------------

    [Fact]
    public void Length_EqualsPathSpanLength()
    {
        var p = VfsPath.From("/local/c/report.pdf:thumbnail?width=200");
        Assert.Equal(p.PathSpan.Length, p.Length);
    }

    [Fact]
    public void Length_ExcludesStreamAndQuery()
    {
        var p = VfsPath.From("/local/c/report.pdf:thumbnail?width=200");
        Assert.Equal("/local/c/report.pdf".Length, p.Length);
    }

    [Fact]
    public void Length_RootPath_IsOne()
    {
        Assert.Equal(1, VfsPath.From("/").Length);
    }

    // -- WithOffset - relative-path slice -------------------------------------

    [Fact]
    public void WithOffset_PathSpanIsSlicedFromNewStart()
    {
        // Simulates what VfsContext.BuildNodeRequest does for mount "/local/c" (length 8).
        // relStart = 8 (mount length) + 1 (skip '/') = 9.
        var full = VfsPath.From("/local/c/sub/file.txt");
        var rel  = full.WithOffset(9); // "sub/file.txt" starts at index 9
        Assert.Equal("sub/file.txt", new string(rel.PathSpan));
    }

    [Fact]
    public void WithOffset_StreamAndQueryRemainAccessible()
    {
        // Stream and query offsets are absolute in the underlying string,
        // so they stay valid after an offset shift.
        var full = VfsPath.From("/local/c/file.txt:stream?q=1");
        var rel  = full.WithOffset(9); // PathSpan = "file.txt"
        Assert.Equal("file.txt", new string(rel.PathSpan));
        Assert.Equal("stream",   new string(rel.StreamSpan));
        Assert.Equal("q=1",      new string(rel.QuerySpan));
    }

    [Fact]
    public void WithOffset_ToStringReturnsSlicedTail()
    {
        // ToString() on a WithOffset path returns everything from the new start onward.
        var full = VfsPath.From("/local/c/file.txt");
        var rel  = full.WithOffset(9);
        Assert.Equal("file.txt", rel.ToString());
    }

    [Fact]
    public void WithOffset_SameStartReturnsThis()
    {
        var p   = VfsPath.From("/local/c/file.txt");
        var rel = p.WithOffset(0);
        Assert.Equal(p, rel);
    }

    [Fact]
    public void WithOffset_NoLeadingSlash()
    {
        var full = VfsPath.From("/local/c/file.txt");
        var rel  = full.WithOffset(9);
        Assert.False(new string(rel.PathSpan).StartsWith('/'));
    }

    // -- VfsNodeRequest - Path (relative) + FullPath ---------------------------

    [Fact]
    public void VfsNodeRequest_PathSpanIsRelativePortion()
    {
        var full     = VfsPath.From("/local/c/sub/file.txt");
        var rel      = full.WithOffset(9);
        var request  = new VfsNodeRequest(rel, full);
        Assert.Equal("sub/file.txt", new string(request.Path.PathSpan));
    }

    [Fact]
    public void VfsNodeRequest_RelativePathSpan()
    {
        var mount   = VfsPath.From("/local/c");
        var full    = VfsPath.From("/local/c/sub/file.txt");
        var request = new VfsNodeRequest(full.WithOffset(9), mount);
        Assert.Equal("sub/file.txt", new string(request.Path.PathSpan));
    }

    [Fact]
    public void VfsNodeRequest_FullPathReconstructedFromMountAndRelative()
    {
        var mount   = VfsPath.From("/local/c");
        var full    = VfsPath.From("/local/c/sub/file.txt");
        var request = new VfsNodeRequest(full.WithOffset(9), mount);
        Assert.Equal("/local/c/sub/file.txt", VfsPath.From(request.Mount, request.Path).ToString());
    }

    [Fact]
    public void VfsNodeRequest_StreamAndQueryFromRelativePath()
    {
        var mount   = VfsPath.From("/local/c");
        var full    = VfsPath.From("/local/c/file.txt:thumb?w=200");
        var request = new VfsNodeRequest(full.WithOffset(9), mount);
        Assert.Equal("file.txt", new string(request.Path.PathSpan));
        Assert.Equal("thumb",    new string(request.Path.StreamSpan));
        Assert.Equal("w=200",    new string(request.Path.QuerySpan));
        Assert.Equal("thumb",    request.Path.GetStreamName());
        Assert.Equal("w=200",    request.Path.GetQueryString());
    }

    [Fact]
    public void VfsNodeRequest_AtMountRoot_RelativePathIsEmpty()
    {
        // When the request path IS the mount point, WithOffset lands past the end:
        // BuildNodeRequest returns default(VfsPath) for the relative path.
        var request = new VfsNodeRequest(default, VfsPath.From("/local/c"));
        Assert.True(request.Path.PathSpan.IsEmpty);
    }

    // -- FillPath / FillStream / FillQuery ------------------------------------

    [Fact]
    public void FillPath_WritesPathSpanIntoBuffer()
    {
        var p = VfsPath.From("/local/c/file.txt:stream?q=1");
        Span<char> buf = stackalloc char[VfsPath.MaxLength];
        int n = p.FillPath(buf);
        Assert.Equal("/local/c/file.txt", new string(buf[..n]));
    }

    [Fact]
    public void FillStream_WritesStreamSpanIntoBuffer()
    {
        var p = VfsPath.From("/local/c/file.txt:stream?q=1");
        Span<char> buf = stackalloc char[VfsPath.MaxLength];
        int n = p.FillStream(buf);
        Assert.Equal("stream", new string(buf[..n]));
    }

    [Fact]
    public void FillQuery_WritesQuerySpanIntoBuffer()
    {
        var p = VfsPath.From("/local/c/file.txt:stream?q=1");
        Span<char> buf = stackalloc char[VfsPath.MaxLength];
        int n = p.FillQuery(buf);
        Assert.Equal("q=1", new string(buf[..n]));
    }

    // -- StartsWith(ReadOnlySpan<char>) ----------------------------------------

    [Fact]
    public void StartsWith_Span_ExactMatch_ReturnsTrue()
    {
        var p = VfsPath.From("/a/b/c");
        Assert.True(p.StartsWith("/a/b/c".AsSpan()));
    }

    [Fact]
    public void StartsWith_Span_PrefixAtSegmentBoundary_ReturnsTrue()
    {
        var p = VfsPath.From("/a/b/c");
        Assert.True(p.StartsWith("/a/b".AsSpan()));
    }

    [Fact]
    public void StartsWith_Span_MidSegment_ReturnsFalse()
    {
        // "/a/bc" should NOT be considered a StartsWith("/a/b") match.
        var p = VfsPath.From("/a/bc");
        Assert.False(p.StartsWith("/a/b".AsSpan()));
    }

    [Fact]
    public void StartsWith_Span_Prefix_Longer_ReturnsFalse()
    {
        var p = VfsPath.From("/a/b");
        Assert.False(p.StartsWith("/a/b/c".AsSpan()));
    }

    [Fact]
    public void StartsWith_Span_Root_AlwaysTrue()
    {
        var p = VfsPath.From("/a/b/c");
        Assert.True(p.StartsWith("/".AsSpan()));
    }

    [Fact]
    public void StartsWith_Span_CaseInsensitiveByDefault()
    {
        var p = VfsPath.From("/A/B/C");
        Assert.True(p.StartsWith("/a/b".AsSpan()));
    }

    [Fact]
    public void StartsWith_Span_IgnoreDifferentSlashes()
    {
        var p = VfsPath.From("\\A/b\\c");
        Assert.True(p.StartsWith("/a/b".AsSpan()));
    }

    [Fact]
    public void StartsWith_Span_CaseSensitive_DifferentCase_ReturnsFalse()
    {
        var p = VfsPath.From("/A/B/C", caseSensitive: true);
        Assert.False(p.StartsWith("/a/b".AsSpan()));
    }

    // -- StartsWith(VfsPath) ---------------------------------------------------

    [Fact]
    public void StartsWith_VfsPath_ExactMatch_ReturnsTrue()
    {
        var path   = VfsPath.From("/a/b/c");
        var prefix = VfsPath.From("/a/b/c");
        Assert.True(path.StartsWith(prefix));
    }

    [Fact]
    public void StartsWith_VfsPath_SegmentBoundary_ReturnsTrue()
    {
        var path   = VfsPath.From("/a/b/c");
        var prefix = VfsPath.From("/a/b");
        Assert.True(path.StartsWith(prefix));
    }

    [Fact]
    public void StartsWith_VfsPath_MidSegment_ReturnsFalse()
    {
        var path   = VfsPath.From("/a/bc");
        var prefix = VfsPath.From("/a/b");
        Assert.False(path.StartsWith(prefix));
    }

    [Fact]
    public void StartsWith_VfsPath_CaseInsensitiveByDefault()
    {
        var path   = VfsPath.From("/LOCAL/C/FILE");
        var prefix = VfsPath.From("/local/c");
        Assert.True(path.StartsWith(prefix));
    }

    [Fact]
    public void StartsWith_VfsPath_EitherCaseSensitive_UsesOrdinal()
    {
        // If either operand is case-sensitive, the check uses Ordinal.
        var path           = VfsPath.From("/LOCAL/C/FILE", caseSensitive: false);
        var caseSensPrefix = VfsPath.From("/local/c",      caseSensitive: true);
        Assert.False(path.StartsWith(caseSensPrefix));
    }

    [Fact]
    public void StartsWith_VfsPath_BothCaseSensitive_ExactMatch_ReturnsTrue()
    {
        var path   = VfsPath.From("/local/c/file", caseSensitive: true);
        var prefix = VfsPath.From("/local/c",      caseSensitive: true);
        Assert.True(path.StartsWith(prefix));
    }

    // -- Equality -------------------------------------------------------------

    [Fact]
    public void Equals_SamePath_ReturnsTrue()
    {
        var a = VfsPath.From("/local/c/file.txt");
        var b = VfsPath.From("/local/c/file.txt");
        Assert.True(a == b);
    }

    [Fact]
    public void Equals_SamePathDifferentSlashes_ReturnsTrue()
    {
        var a = VfsPath.From("/local/c/file.txt");
        var b = VfsPath.From("/local\\h\\..\\c/file.txt");
        Assert.True(a == b);
    }

    [Fact]
    public void Equals_DifferentCase_CaseInsensitive_ReturnsTrue()
    {
        var a = VfsPath.From("/LOCAL/C/FILE.TXT");
        var b = VfsPath.From("/local/c/file.txt");
        Assert.True(a == b);
    }

    [Fact]
    public void Equals_DifferentCase_CaseSensitive_ReturnsFalse()
    {
        var a = VfsPath.From("/LOCAL/C/FILE.TXT", caseSensitive: true);
        var b = VfsPath.From("/local/c/file.txt", caseSensitive: true);
        Assert.False(a == b);
    }

    [Fact]
    public void Equals_QueryIgnored()
    {
        // Equality ignores query strings - same path, different queries → equal.
        var a = VfsPath.From("/local/c/file.txt?v=1"); // TOOD: need to look into this.
        var b = VfsPath.From("/local/c/file.txt?v=2");
        Assert.True(a == b);
    }

    [Fact]
    public void Equals_StreamIncluded()
    {
        // Equality includes the ADS stream - same path, different stream → not equal.
        var a = VfsPath.From("/local/c/file.txt:thumb");
        var b = VfsPath.From("/local/c/file.txt:preview");
        Assert.False(a == b);
    }

    [Fact]
    public void Equals_WithStreamVsWithout_NotEqual()
    {
        var a = VfsPath.From("/local/c/file.txt:thumb");
        var b = VfsPath.From("/local/c/file.txt");
        Assert.False(a == b);
    }

    [Fact]
    public void Equals_DifferentPaths_ReturnsFalse()
    {
        var a = VfsPath.From("/local/c/file.txt");
        var b = VfsPath.From("/local/c/other.txt");
        Assert.False(a == b);
    }

    [Fact]
    public void NotEquals_Operator_Works()
    {
        var a = VfsPath.From("/a");
        var b = VfsPath.From("/b");
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_UnicodeAccents_CaseInsensitive_AsciiLettersFold()
    {
        // OrdinalIgnoreCase folds ASCII letters. Accented chars are compared by
        // code unit - NFC 'é' (U+00E9) vs NFC 'É' (U+00C9) differ only in ASCII case
        // of the surrounding letters so the full path can still match.
        var a = VfsPath.From("/café/file.txt");
        var b = VfsPath.From("/café/file.txt");
        Assert.True(a == b);
    }

    [Fact]
    public void Equals_NfcVsNfd_NotEqual()
    {
        // NFC 'é' (U+00E9) and NFD 'é' (U+0065 U+0301) have different code units.
        // VfsPath uses OrdinalIgnoreCase which does NOT Unicode-normalize, so they differ.
        var nfc = VfsPath.From("/café/file.txt");   // é as single code point
        var nfd = VfsPath.From("/café/file.txt");  // e + combining acute accent
        Assert.False(nfc == nfd);
    }

    // -- GetHashCode -----------------------------------------------------------

    [Fact]
    public void GetHashCode_EqualPaths_SameHash()
    {
        var a = VfsPath.From("/local/c/file.txt");
        var b = VfsPath.From("/LOCAL/C/FILE.TXT");
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_UsableInDictionary()
    {
        var dict = new Dictionary<VfsPath, int>();
        var key  = VfsPath.From("/local/c/file.txt");
        dict[key] = 42;
        Assert.Equal(42, dict[VfsPath.From("/LOCAL/C/FILE.TXT")]);
    }

    [Fact]
    public void GetHashCode_QueryIgnored_SameAsWithout()
    {
        var withQuery    = VfsPath.From("/local/c/file.txt?v=1");
        var withoutQuery = VfsPath.From("/local/c/file.txt");
        Assert.Equal(withQuery.GetHashCode(), withoutQuery.GetHashCode());
    }

    // -- IsCaseSensitive flag --------------------------------------------------

    [Fact]
    public void IsCaseSensitive_DefaultFalse()
    {
        var p = VfsPath.From("/local/c/file.txt");
        Assert.False(p.IsCaseSensitive);
    }

    [Fact]
    public void IsCaseSensitive_SetTrue_RoundTrips()
    {
        var p = VfsPath.From("/local/c/file.txt", caseSensitive: true);
        Assert.True(p.IsCaseSensitive);
    }

    // -- From(VfsPath) - identity ----------------------------------------------

    [Fact]
    public void From_VfsPath_ReturnsSameValue()
    {
        var original = VfsPath.From("/local/c/file.txt");
        var copy     = VfsPath.From(original);
        Assert.Equal(original, copy);
        Assert.Equal(original.ToString(), copy.ToString());
    }

    [Fact]
    public void From_VfsPath_PreservesCaseSensitivity()
    {
        var original = VfsPath.From("/local/c/file.txt", caseSensitive: true);
        var copy     = VfsPath.From(original);
        Assert.True(copy.IsCaseSensitive);
    }

    // -- From(VfsPath, string) - join -----------------------------------------

    [Fact]
    public void From_VfsPathBase_RelativeString_Joined()
    {
        var base_ = VfsPath.From("/local/c");
        var p     = VfsPath.From(base_, "sub/file.txt");
        Assert.Equal("/local/c/sub/file.txt", p.ToString());
    }

    [Fact]
    public void From_VfsPathBase_AbsoluteString_BaseIgnored()
    {
        var base_ = VfsPath.From("/local/c");
        var p     = VfsPath.From(base_, "/other/path");
        Assert.Equal("/other/path", p.ToString());
    }

    [Fact]
    public void From_VfsPathBase_BackslashRelative_Normalised()
    {
        var base_ = VfsPath.From("/local/c");
        var p     = VfsPath.From(base_, @"sub\file.txt");
        Assert.Equal("/local/c/sub/file.txt", p.ToString());
    }

    [Fact]
    public void From_VfsPathBase_DotDotInRelative_Resolved()
    {
        var base_ = VfsPath.From("/local/c/sub");
        var p     = VfsPath.From(base_, "../other/file.txt");
        Assert.Equal("/local/c/other/file.txt", p.ToString());
    }

    [Fact]
    public void From_VfsPathBase_InheritsBaseCaseSensitivity()
    {
        var base_ = VfsPath.From("/local/c", caseSensitive: true);
        var p     = VfsPath.From(base_, "file.txt");
        Assert.True(p.IsCaseSensitive);
    }

    // -- From(VfsPath, VfsPath) - join ----------------------------------------

    [Fact]
    public void From_VfsPathBase_VfsPathRelative_Joined()
    {
        // From("sub/file.txt") is now genuinely relative - joining works naturally.
        var base_ = VfsPath.From("/local/c");
        var rel   = VfsPath.From("sub/file.txt");
        var p     = VfsPath.From(base_, rel);
        Assert.Equal("/local/c/sub/file.txt", p.ToString());
    }

    [Fact]
    public void From_VfsPathBase_AbsoluteVfsPath_BaseIgnored()
    {
        var base_ = VfsPath.From("/local/c");
        var abs   = VfsPath.From("/other/path");
        var p     = VfsPath.From(base_, abs);
        Assert.Equal("/other/path", p.ToString());
    }

    [Fact]
    public void From_VfsPathBase_VfsPathRelative_CaseSensitivityUnion()
    {
        // If either side is case-sensitive, result is case-sensitive.
        var base_ = VfsPath.From("/local/c",  caseSensitive: false);
        var rel   = VfsPath.From("file.txt",  caseSensitive: true);
        var p     = VfsPath.From(base_, rel);
        Assert.True(p.IsCaseSensitive);
    }

    // -- From(string, VfsPath) - relative to VfsPath currentDirectory ---------

    [Fact]
    public void From_StringRelative_VfsPathBase_Joined()
    {
        var dir = VfsPath.From("/local/c");
        var p   = VfsPath.From("sub/file.txt", dir);
        Assert.Equal("/local/c/sub/file.txt", p.ToString());
    }

    [Fact]
    public void From_StringAbsolute_VfsPathBase_BaseIgnored()
    {
        var dir = VfsPath.From("/local/c");
        var p   = VfsPath.From("/other/path", dir);
        Assert.Equal("/other/path", p.ToString());
    }

    [Fact]
    public void From_StringRelativeWithBackslash_VfsPathBase_Normalised()
    {
        var dir = VfsPath.From("/local/c");
        var p   = VfsPath.From(@"sub\file.txt", dir);
        Assert.Equal("/local/c/sub/file.txt", p.ToString());
    }

    // -- From(string, string?) - relative to string currentDirectory ----------

    [Fact]
    public void From_RelativePath_WithStringBase_Joined()
    {
        var p = VfsPath.From("file.txt", "/local/c");
        Assert.Equal("/local/c/file.txt", p.ToString());
    }

    [Fact]
    public void From_AbsolutePath_WithStringBase_BaseIgnored()
    {
        var p = VfsPath.From("/other/path", "/local/c");
        Assert.Equal("/other/path", p.ToString());
    }

    [Fact]
    public void From_RelativePath_NullBase_StaysRelative()
    {
        // No currentDirectory - relative path stays relative.
        // Callers that need absolute resolution should supply a base.
        var p = VfsPath.From("file.txt", (string?)null);
        Assert.Equal("file.txt", p.ToString());
        Assert.True(p.IsRelative);
    }

    // -- Normalize(string) -----------------------------------------------------

    [Fact]
    public void Normalize_CleanPath_ReturnsSameContent()
    {
        Assert.Equal("/local/c/file.txt", VfsPath.Normalize("/local/c/file.txt"));
    }

    [Fact]
    public void Normalize_Backslashes_NormalisedToForwardSlash()
    {
        Assert.Equal("/local/c/file.txt", VfsPath.Normalize(@"\local\c\file.txt"));
    }

    [Fact]
    public void Normalize_DotSegments_Resolved()
    {
        Assert.Equal("/local/c/file.txt", VfsPath.Normalize("/local/c/./sub/../file.txt"));
    }

    // -- Error cases -----------------------------------------------------------

    [Fact]
    public void From_NullString_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => VfsPath.From((string)null!));
    }

    [Fact]
    public void From_PathExceedingMaxLength_ThrowsArgumentException()
    {
        var tooLong = "/" + new string('a', VfsPath.MaxLength);
        Assert.Throws<ArgumentException>(() => VfsPath.From(tooLong));
    }

    // -- Default / empty struct ------------------------------------------------

    [Fact]
    public void Default_PathSpan_IsEmpty()
    {
        var p = default(VfsPath);
        Assert.True(p.PathSpan.IsEmpty);
    }

    [Fact]
    public void Default_ToString_ReturnsSlash()
    {
        // default(VfsPath) has a null _value; ToString falls back to "/".
        var p = default(VfsPath);
        Assert.Equal("/", p.ToString());
    }

    [Fact]
    public void Default_Length_IsZero()
    {
        var p = default(VfsPath);
        Assert.Equal(0, p.Length);
    }

    // -- Rebase ----------------------------------------------------------------

    [Fact]
    public void Rebase_ExactPrefixMatch_ReturnsNewBase()
    {
        // path == prefix exactly → result is just the new base.
        var path   = VfsPath.From("/docs");
        var prefix = VfsPath.From("/docs");
        var newBase = VfsPath.From("/data/documents");
        Assert.Equal("/data/documents", VfsPath.Rebase(path, prefix, newBase).ToString());
    }

    [Fact]
    public void Rebase_PrefixWithSuffix_SuffixAppended()
    {
        var path    = VfsPath.From("/docs/reports/q4.pdf");
        var prefix  = VfsPath.From("/docs");
        var newBase = VfsPath.From("/data/documents");
        Assert.Equal("/data/documents/reports/q4.pdf", VfsPath.Rebase(path, prefix, newBase).ToString());
    }

    [Fact]
    public void Rebase_RootPrefix_PrependNewBase()
    {
        // Rebasing a path under "/" onto a non-root base.
        var path    = VfsPath.From("/file.txt");
        var prefix  = VfsPath.From("/");
        var newBase = VfsPath.From("/mnt/fs");
        Assert.Equal("/mnt/fs/file.txt", VfsPath.Rebase(path, prefix, newBase).ToString());
    }

    [Fact]
    public void Rebase_DeepSuffix_NormalisedCorrectly()
    {
        var path    = VfsPath.From("/alias/a/b/c/file.txt");
        var prefix  = VfsPath.From("/alias");
        var newBase = VfsPath.From("/real");
        Assert.Equal("/real/a/b/c/file.txt", VfsPath.Rebase(path, prefix, newBase).ToString());
    }

    [Fact]
    public void Rebase_PreservesIsCaseSensitive_WhenPathIsCaseSensitive()
    {
        var path    = VfsPath.From("/docs/file.txt", caseSensitive: true);
        var prefix  = VfsPath.From("/docs");
        var newBase = VfsPath.From("/data");
        Assert.True(VfsPath.Rebase(path, prefix, newBase).IsCaseSensitive);
    }

    [Fact]
    public void Rebase_PreservesIsCaseSensitive_WhenNewBaseIsCaseSensitive()
    {
        var path    = VfsPath.From("/docs/file.txt");
        var prefix  = VfsPath.From("/docs");
        var newBase = VfsPath.From("/data", caseSensitive: true);
        Assert.True(VfsPath.Rebase(path, prefix, newBase).IsCaseSensitive);
    }

    [Fact]
    public void Rebase_CaseInsensitive_WhenNeitherIsCaseSensitive()
    {
        var path    = VfsPath.From("/docs/file.txt");
        var prefix  = VfsPath.From("/docs");
        var newBase = VfsPath.From("/data");
        Assert.False(VfsPath.Rebase(path, prefix, newBase).IsCaseSensitive);
    }

    [Fact]
    public void Rebase_ResultIsAbsolute_WhenNewBaseIsAbsolute()
    {
        var path    = VfsPath.From("/alias/sub/file.txt");
        var prefix  = VfsPath.From("/alias");
        var newBase = VfsPath.From("/real");
        Assert.True(VfsPath.Rebase(path, prefix, newBase).IsAbsolute);
    }

    [Fact]
    public void Rebase_NoLeadingSlashInSuffix_JoinedCleanly()
    {
        // Internal: suffix from path[prefix.Length..] starts with '/' which is
        // trimmed by CombineSpans - verify no double slash in result.
        var path    = VfsPath.From("/a/b/c");
        var prefix  = VfsPath.From("/a");
        var newBase = VfsPath.From("/x");
        Assert.Equal("/x/b/c", VfsPath.Rebase(path, prefix, newBase).ToString());
        Assert.DoesNotContain("//", VfsPath.Rebase(path, prefix, newBase).ToString());
    }

    // -- NameSpan / GetName / WithName -----------------------------------------

    [Fact]
    public void NameSpan_MultiSegment_ReturnsLastSegment()
    {
        var p = VfsPath.From("/a/b/report.pdf");
        Assert.Equal("report.pdf", new string(p.NameSpan));
    }

    [Fact]
    public void NameSpan_SingleSegment_ReturnsSelf()
    {
        var p = VfsPath.From("file.txt");
        Assert.Equal("file.txt", new string(p.NameSpan));
    }

    [Fact]
    public void NameSpan_Root_ReturnsEmpty()
    {
        var p = VfsPath.From("/");
        Assert.True(p.NameSpan.IsEmpty);
    }

    [Fact]
    public void NameSpan_Default_ReturnsEmpty()
    {
        Assert.True(default(VfsPath).NameSpan.IsEmpty);
    }

    [Fact]
    public void GetName_ReturnsString()
    {
        var p = VfsPath.From("/a/b/report.pdf");
        Assert.Equal("report.pdf", p.GetName());
    }

    [Fact]
    public void WithName_MultiSegment_ReplacesLastSegment()
    {
        var p   = VfsPath.From("/a/b/old.txt");
        var out_ = p.WithName("new.txt");
        Assert.Equal("/a/b/new.txt", out_.ToString());
    }

    [Fact]
    public void WithName_SingleSegment_NoParent_ReusesString()
    {
        // Fast path: no '/' in path - From(newName) returned directly.
        var p    = VfsPath.From("old.txt");
        var out_ = p.WithName("new.txt");
        Assert.Equal("new.txt", out_.ToString());
    }

    [Fact]
    public void WithName_MountRelative_NoParent_ReturnsJustName()
    {
        // Mount-relative path with no subdirectory - sibling rename.
        var p    = VfsPath.From("file.dat").WithOffset(0); // already relative
        var out_ = p.WithName("dst.dat");
        Assert.Equal("dst.dat", out_.ToString());
    }

    [Fact]
    public void WithName_DropsStreamAndQuery()
    {
        var p    = VfsPath.From("/a/b/file.txt:thumb?w=200");
        var out_ = p.WithName("other.txt");
        Assert.Equal("/a/b/other.txt", out_.ToString());
        Assert.True(out_.StreamSpan.IsEmpty);
        Assert.True(out_.QuerySpan.IsEmpty);
    }

    // -- Drive prefixes (Windows / named drives) -------------------------------
    //
    // "<name>:" at position 0, when the colon is followed by a separator or the
    // end of input, is rewritten to a leading "/<name>" segment (name folded to
    // lower-case). A colon in any other position stays an ADS stream separator.

    [Theory]
    [InlineData(@"C:\",                    "/c")]
    [InlineData(@"C:",                     "/c")]
    [InlineData(@"C:\report.pdf",          "/c/report.pdf")]
    [InlineData(@"C:\a\b",                 "/c/a/b")]
    [InlineData(@"C:/a/b",                 "/c/a/b")]        // forward slashes too
    [InlineData(@"c:\a\b",                 "/c/a/b")]        // already lower-case
    [InlineData(@"azure:\data\file.txt",   "/azure/data/file.txt")]
    [InlineData(@"azure:",                 "/azure")]
    [InlineData(@"s3:/bucket/key",         "/s3/bucket/key")]
    public void From_DrivePrefix_NormalizesToLeadingSegment(string input, string expected)
    {
        var p = VfsPath.From(input);
        Assert.Equal(expected, p.ToString());
        Assert.True(p.IsAbsolute);
    }

    [Fact]
    public void From_DrivePrefix_PreservesRealAdsStreamAndQuery()
    {
        // The drive colon is consumed; the ADS colon in the final segment survives.
        var p = VfsPath.From(@"C:\file.txt:thumb?w=200");
        Assert.Equal("/c/file.txt:thumb?w=200", p.ToString());
        Assert.Equal("/c/file.txt", new string(p.PathSpan));
        Assert.Equal("thumb",       new string(p.StreamSpan));
        Assert.Equal("w=200",       new string(p.QuerySpan));
    }

    [Fact]
    public void From_DrivePrefix_EquivalentToLeadingSlashForm()
    {
        Assert.Equal(VfsPath.From("/c/x"), VfsPath.From(@"C:\x"));
    }

    [Fact]
    public void From_DrivePrefix_EquivalentThroughDotSegmentSlowPath()
    {
        // Forces the equality slow-path (dot segment) - proves NormalizeToSpan
        // applies the same drive rewrite as NormalizeCore.
        Assert.Equal(VfsPath.From("/c/x"), VfsPath.From(@"C:\a\..\x"));
    }

    [Theory]
    [InlineData("report:thumb")]   // colon not followed by a separator → ADS, relative
    [InlineData("a/b:thumb")]      // colon not at position 0 → ADS in last segment
    public void From_ColonNotDrivePrefix_StaysAdsStream(string input)
    {
        var p = VfsPath.From(input);
        Assert.False(p.IsAbsolute);
        Assert.Equal("thumb", new string(p.StreamSpan));
    }
}
