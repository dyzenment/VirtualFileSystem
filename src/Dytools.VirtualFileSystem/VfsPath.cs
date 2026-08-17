using System.Buffers;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Immutable, stack-allocated VFS path handle - 16 bytes on 64-bit runtimes.
///
/// Two fields only: a string reference (8 bytes) and a packed long (8 bytes).
/// Every path operation is pure span arithmetic or integer bit-extraction - no
/// heap allocation for clean paths.
///
/// Example:  /local/c/report.pdf:thumbnail?width=200
///   PathSpan     = "/local/c/report.pdf"   (base path, no stream or query)
///   StreamSpan   = "thumbnail"             (ADS stream name, without ':')
///   QuerySpan    = "width=200"             (query string, without '?')
///
/// Equality considers path + stream, ignores query.
/// Comparisons are OrdinalIgnoreCase by default; pass caseSensitive:true to opt in.
/// </summary>
public readonly struct VfsPath : IEquatable<VfsPath>
{
    /// <summary>Maximum total path length accepted by <see cref="From(string,bool)"/>.</summary>
    public const int MaxLength = 511; // ALWAYS MAKE THIS ONE LESS THAN A POWER OF TWO (e.g. 511, 1023, 2047) TO AVOID OFF-BY-ONE ISSUES IN THE OFFSET FIELDS. MAX IS 8095


    // -- Bit layout of _packed (long, 64 bits) ---------------------------------
    //
    // ┌- To extend MaxLength: increase OFFSET_BITS (one constant change).
    // │  MaxLength must be ≤ (1 << OFFSET_BITS).
    // │  OFFSET_BITS = 11 → supports MaxLength up to 2047.
    // │  OFFSET_BITS = 12 → supports MaxLength up to 4095.
    // │
    // ├- To add a flag bit: change HASH_SHIFT to OFFSET_BITS*3 + <flag count>.
    // │  HASH_BITS drops by 1 per added flag. Current spare: 36 hash bits.
    // │
    // └- Boundary map (OFFSET_BITS = 9):
    //    bits  0–8    _start          (9 bits - index into _value where slice begins)
    //    bits  9–17   _streamOffset   (9 bits - index of ':' or equals _queryOffset)
    //    bits 18–26   _queryOffset    (9 bits - index of '?' or equals _value.Length)
    //    bit  27      IsCaseSensitive
    //    bits 28–63   partial hash    (36 bits, false-positive rate ≈ 1 in 68 billion)
    //
    // NOTE: _queryOffset can equal _value.Length (no query present), so the offset
    // field must be able to represent MaxLength exactly - hence OFFSET_BITS = 9 is
    // required to support MaxLength = 511 (8 bits only reaches 255).

    private const int OFFSET_BITS = MaxLength < 512  ?  9   // 0..511
        : MaxLength < 1024 ? 10   // 0..1023
        : MaxLength < 2048 ? 11   // 0..2047
        : MaxLength < 4096 ? 12   // 0..4095
        :                   13;   // 0..8191
    private const long OFFSET_MASK = (1L << OFFSET_BITS) - 1;

    private const int  START_SHIFT  = 0;
    private const int  STREAM_SHIFT = OFFSET_BITS;             // 9
    private const int  QUERY_SHIFT  = OFFSET_BITS * 2;         // 18

    // Flag bits - sit immediately after the three offset fields.
    private const int  CASE_SHIFT   = OFFSET_BITS * 3;         // 27
    private const long CASE_BIT     = 1L << CASE_SHIFT;

    // Partial hash occupies all remaining bits above the flags.
    // Increasing OFFSET_BITS or adding flag bits reduces HASH_BITS.
    private const int  HASH_SHIFT    = OFFSET_BITS * 3 + 1;   // 28
    private const int  HASH_BITS     = 64 - HASH_SHIFT;       // 36
    private const long HASH_MASK_RAW = (1L << HASH_BITS) - 1; // mask for the 36-bit value

    // -- Fields ----------------------------------------------------------------

    // Fully-normalised path string. For already-clean paths this IS the original
    // string reference - no allocation. Null only for default(VfsPath).
    private readonly string _value;

    // All metadata in one word. See bit-layout table above.
    private readonly long _packed;

    // -- Private decoded accessors (named-field view; also visible in debugger) -

    private int Start        => (int)((_packed >> START_SHIFT)  & OFFSET_MASK);
    private int StreamOffset => (int)((_packed >> STREAM_SHIFT) & OFFSET_MASK);
    private int QueryOffset  => (int)((_packed >> QUERY_SHIFT)  & OFFSET_MASK);
    private long PartialHash => (_packed >> HASH_SHIFT) & HASH_MASK_RAW;

    // -- Constructor -----------------------------------------------------------

    // Packs all components and eagerly computes the partial hash.
    // Hash covers value[start..queryOffset] = path + optional ":stream" (no query).
    private VfsPath(string value, int start, int streamOffset, int queryOffset, bool caseSensitive)
    {
        _value = value;

        var comp     = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var hashSpan = value is null
            ? ReadOnlySpan<char>.Empty
            : value.AsSpan(start, queryOffset - start); // path + ":stream" if present
        // Empty content hashes to 0 so every empty path shares default(VfsPath)'s packed-0 hash -
        // otherwise the Equals fast-path would reject default vs From(""). Non-empty paths use the
        // real hash; empty paths are all equal anyway, so this doesn't weaken the filter.
        var h = hashSpan.IsEmpty ? 0L : (long)(uint)string.GetHashCode(hashSpan, comp);

        _packed = ((long)start        << START_SHIFT)
                | ((long)streamOffset << STREAM_SHIFT)
                | ((long)queryOffset  << QUERY_SHIFT)
                | (caseSensitive ? CASE_BIT : 0L)
                | ((h & HASH_MASK_RAW) << HASH_SHIFT);
    }

    // -- Factory methods -------------------------------------------------------

    /// <summary>Length of the base path (PathSpan.Length). Used for mount-prefix sort.</summary>
    public int Length => PathSpan.Length;

    /// <summary>True when the path starts with '/'. False for relative paths and default(VfsPath).</summary>
    public bool IsAbsolute => PathSpan is { Length: > 0 } s && s[0] == '/';

    /// <summary>True when the path does not start with '/'. Relative paths must be joined with a
    /// base before being passed to the registry or pipeline.</summary>
    public bool IsRelative => !IsAbsolute;

    /// <summary>Identity - returns the same value unchanged.</summary>
    public static VfsPath From(VfsPath path) => path;

    /// <summary>Joins <paramref name="base"/> with a relative <paramref name="path"/> string.</summary>
    public static VfsPath From(VfsPath @base, string path, bool caseSensitive = false)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        var raw = path.AsSpan();
        if (StartsAbsolute(raw))
            return From(path, caseSensitive);
        return CombineSpans(@base.PathSpan, raw, caseSensitive || @base.IsCaseSensitive);
    }

    /// <summary>Joins <paramref name="base"/> with a <paramref name="relative"/> VfsPath.</summary>
    public static VfsPath From(VfsPath @base, VfsPath relative)
    {
        var rel = relative.PathSpan;
        if (rel.Length > 0 && rel[0] == '/') return relative;
        return CombineSpans(@base.PathSpan, rel, @base.IsCaseSensitive || relative.IsCaseSensitive);
    }

    /// <summary>Normalises <paramref name="path"/> relative to a VfsPath current directory.</summary>
    public static VfsPath From(string path, VfsPath currentDirectory, bool caseSensitive = false)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        var raw = path.AsSpan();
        if (StartsAbsolute(raw))
            return From(path, caseSensitive);
        return CombineSpans(currentDirectory.PathSpan, raw, caseSensitive || currentDirectory.IsCaseSensitive);
    }

    /// <summary>
    /// Normalises <paramref name="path"/> and returns a VfsPath.
    /// Zero heap allocation when the path is already canonical
    /// (absolute, forward-slash separators, no dot segments, no trailing slash).
    /// </summary>
    public static VfsPath From(string path, bool caseSensitive = false)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (path.Length > MaxLength)
            throw new ArgumentException(
                $"VFS path exceeds the {MaxLength}-character limit.", nameof(path));

        var normalized = NormalizeCore(path, path.AsSpan(), out int so, out int qo);
        return new VfsPath(normalized, 0, so, qo, caseSensitive);
    }

    /// <summary>
    /// Normalises <paramref name="path"/> relative to <paramref name="currentDirectory"/> and returns a VfsPath.
    /// When <paramref name="path"/> is absolute (starts with '/' or '\') the current directory is ignored.
    /// When <paramref name="path"/> is relative it is joined with <paramref name="currentDirectory"/> first.
    /// A null or empty <paramref name="currentDirectory"/> treats relative paths as rooted from '/'.
    /// </summary>
    public static VfsPath From(string path, string? currentDirectory, bool caseSensitive = false)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));

        var raw = path.AsSpan();

        // Absolute path or no base to join with - normalise directly.
        if (currentDirectory is null || StartsAbsolute(raw))
            return From(path, caseSensitive);

        // Relative path: prepend currentDirectory and normalise in one pass.
        var baseSpan = currentDirectory.AsSpan().TrimEnd('/');
        int totalLen = baseSpan.Length + 1 + raw.Length;
        if (totalLen > MaxLength)
            throw new ArgumentException(
                $"VFS path exceeds the {MaxLength}-character limit.", nameof(path));

        Span<char> buf = stackalloc char[MaxLength];
        baseSpan.CopyTo(buf);
        buf[baseSpan.Length] = '/';
        raw.CopyTo(buf[(baseSpan.Length + 1)..]);

        // Pass null as original - no single string ref represents the combined path.
        var normalized = NormalizeCore(null, buf[..totalLen], out int so, out int qo);
        return new VfsPath(normalized, 0, so, qo, caseSensitive);
    }

    /// <summary>
    /// Normalises the span content and returns a VfsPath.
    /// Always allocates the underlying string (no original reference to reuse).
    /// Useful when building paths from stack-allocated span buffers.
    /// </summary>
    public static VfsPath From(ReadOnlySpan<char> path, bool caseSensitive = false)
    {
        if (path.Length > MaxLength)
            throw new ArgumentException(
                $"VFS path exceeds the {MaxLength}-character limit.", nameof(path));

        var normalized = NormalizeCore(null, path, out int so, out int qo);
        return new VfsPath(normalized, 0, so, qo, caseSensitive);
    }

    /// <summary>
    /// Normalises <paramref name="path"/> and returns the canonical string.
    /// Returns the original reference when the path is already canonical (zero allocation).
    /// </summary>
    public static string Normalize(string path) => From(path).ToString();

    // -- Internal plumbing -----------------------------------------------------

    /// <summary>
    /// Returns a copy of this VfsPath with <see cref="Start"/> shifted to <paramref name="newStart"/>,
    /// producing a view into a sub-portion of the same underlying string.
    ///
    /// <see cref="StreamOffset"/> and <see cref="QueryOffset"/> are preserved as absolute positions,
    /// so <see cref="StreamSpan"/> and <see cref="QuerySpan"/> remain valid on the returned slice.
    /// <see cref="PathSpan"/> becomes <c>value[newStart..StreamOffset]</c>.
    ///
    /// Use this to create a zero-alloc relative-path view from a fully-resolved path:
    /// the relative VfsPath shares the same underlying string - no heap allocation.
    /// The partial hash is recomputed because <see cref="PathSpan"/> changes.
    /// </summary>
    public VfsPath WithOffset(int newStart)
    {
        if (newStart == Start) return this;
        return new VfsPath(_value, newStart, StreamOffset, QueryOffset, IsCaseSensitive);
    }

    // -- Properties ------------------------------------------------------------

    /// <summary>True when comparisons use Ordinal (case-sensitive) rather than OrdinalIgnoreCase.</summary>
    public bool IsCaseSensitive => (_packed & CASE_BIT) != 0;

    // -- Zero-alloc span accessors ---------------------------------------------

    /// <summary>
    /// Base path without ADS stream or query string.
    /// E.g. "/local/c/report.pdf" from "/local/c/report.pdf:thumbnail?width=200".
    /// </summary>
    public ReadOnlySpan<char> PathSpan
        => _value is null ? ReadOnlySpan<char>.Empty
            : _value.AsSpan(Start, StreamOffset - Start);

    /// <summary>
    /// ADS stream name without the ':' separator. Empty when no stream is present.
    /// E.g. "thumbnail" from "/local/c/report.pdf:thumbnail?width=200".
    /// </summary>
    public ReadOnlySpan<char> StreamSpan
    {
        get
        {
            var so = StreamOffset;
            var qo = QueryOffset;
            return _value is not null && so < qo
                ? _value.AsSpan(so + 1, qo - so - 1)
                : ReadOnlySpan<char>.Empty;
        }
    }

    /// <summary>
    /// Query string without the leading '?'. Empty when no query is present.
    /// E.g. "width=200" from "/local/c/report.pdf:thumbnail?width=200".
    /// </summary>
    public ReadOnlySpan<char> QuerySpan
    {
        get
        {
            var qo = QueryOffset;
            return _value is not null && qo < _value.Length
                ? _value.AsSpan(qo + 1)
                : ReadOnlySpan<char>.Empty;
        }
    }

    // -- Zero-alloc fill functions (caller provides the stackalloc buffer) ------

    /// <summary>Copies <see cref="PathSpan"/> into <paramref name="buf"/>. Returns chars written.</summary>
    public int FillPath(Span<char> buf)   { var s = PathSpan;   s.CopyTo(buf); return s.Length; }

    /// <summary>Copies <see cref="StreamSpan"/> into <paramref name="buf"/>. Returns chars written.</summary>
    public int FillStream(Span<char> buf) { var s = StreamSpan; s.CopyTo(buf); return s.Length; }

    /// <summary>Copies <see cref="QuerySpan"/> into <paramref name="buf"/>. Returns chars written.</summary>
    public int FillQuery(Span<char> buf)  { var s = QuerySpan;  s.CopyTo(buf); return s.Length; }

    // -- String materializations (allocate) ------------------------------------

    /// <summary>ADS stream name, or null when no stream is present.</summary>
    public string? GetStreamName()
    {
        var so = StreamOffset;
        var qo = QueryOffset;
        return _value is not null && so < qo ? new string(_value.AsSpan(so + 1, qo - so - 1)) : null;
    }

    /// <summary>Query string without leading '?', or null when none is present.</summary>
    public string? GetQueryString()
    {
        var qo = QueryOffset;
        return _value is not null && qo < _value.Length ? new string(_value.AsSpan(qo + 1)) : null;
    }

    /// <summary>
    /// Full normalised path string from start to end (includes stream and query).
    /// Zero allocation for full paths (start == 0) - returns the stored value directly.
    /// </summary>
    public override string ToString()
    {
        // default(VfsPath) is the empty relative path (empty PathSpan, IsRelative), so it
        // stringifies to "" - consistent with the other members and round-tripping through
        // From(""). The absolute root is VfsPath.From("/"), which has a non-null "/" value.
        var s = Start;
        return _value is null ? "" : s == 0 ? _value : new string(_value.AsSpan(s));
    }

    // -- Rebase (alias expansion helper) --------------------------------------

    /// <summary>
    /// Strips <paramref name="oldPrefix"/> from <paramref name="path"/> and prepends
    /// <paramref name="newBase"/>. Caller guarantees <c>path.StartsWith(oldPrefix)</c>.
    /// </summary>
    public static VfsPath Rebase(VfsPath path, VfsPath oldPrefix, VfsPath newBase)
    {
        var suffix = path.PathSpan[oldPrefix.Length..];
        if (suffix.IsEmpty) return newBase;
        return CombineSpans(newBase.PathSpan, suffix.TrimStart('/'),
            newBase.IsCaseSensitive || path.IsCaseSensitive);
    }

    // -- Prefix check with segment-boundary guard ------------------------------

    /// <summary>
    /// True when this path's base portion begins with <paramref name="prefix"/>
    /// at a clean segment boundary.
    /// "/a/b/c".StartsWith("/a/b") → true; "/a/bc".StartsWith("/a/b") → false.
    /// </summary>
    public bool StartsWith(ReadOnlySpan<char> prefix)
    {
        var self = PathSpan;
        var comp = IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!MemoryExtensions.StartsWith(self, prefix, comp)) return false;
        if (self.Length == prefix.Length) return true;
        // Segment-boundary guard: the character immediately after the prefix in self
        // must be '/', OR the prefix itself ends with '/' (covers the root "/" case).
        return self[prefix.Length] == '/' || prefix[prefix.Length - 1] == '/';
    }

    /// <summary><see cref="StartsWith(ReadOnlySpan{char})"/> overload accepting another VfsPath.</summary>
    public bool StartsWith(VfsPath prefix)
    {
        var comp = (IsCaseSensitive || prefix.IsCaseSensitive)
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var self = PathSpan;
        var pre  = prefix.PathSpan;
        if (!MemoryExtensions.StartsWith(self, pre, comp)) return false;
        if (self.Length == pre.Length) return true;
        return self[pre.Length] == '/' || pre[pre.Length - 1] == '/';
    }

    /// <summary>
    /// The final path segment (filename or directory name), as a zero-alloc span.
    /// E.g. "report.pdf" from "folder/report.pdf" or "/a/b/report.pdf".
    /// Empty for default(VfsPath) or a bare root "/".
    /// </summary>
    public ReadOnlySpan<char> NameSpan
    {
        get
        {
            var path = PathSpan;
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }
    }

    /// <summary>Materializes <see cref="NameSpan"/> as a string.</summary>
    public string GetName() => new string(NameSpan);

    /// <summary>
    /// Returns a new VfsPath with the final segment replaced by <paramref name="newName"/>.
    /// Stream and query are not preserved - renaming a file clears ADS address.
    /// Zero-alloc when there is no parent segment (reuses <paramref name="newName"/> string directly).
    /// </summary>
    public VfsPath WithName(string newName)
    {
        if (newName is null) throw new ArgumentNullException(nameof(newName));
        var path = PathSpan;
        int slash = path.LastIndexOf('/');
        if (slash < 0) return From(newName, IsCaseSensitive);
        return CombineSpans(path[..slash], newName.AsSpan(), IsCaseSensitive);
    }
    
    // -- Equality --------------------------------------------------------------
    //
    // Equality covers path + stream, ignores query.
    // The 36-bit partial hash stored in _packed gives a fast inequality check:
    // unequal paths short-circuit without touching the span at all.
    // Un-normalised dot segments in either operand are resolved lazily via a
    // stackalloc normalisation pass - no heap allocation even for messy paths.

    public bool Equals(VfsPath other)
    {
        // Fast inequality: hash mismatch → definitely not equal (zero false negatives).
        if (PartialHash != other.PartialHash) return false;

        var comp = (IsCaseSensitive || other.IsCaseSensitive)
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return EqualsNormalized(PathSpan, StreamSpan, other.PathSpan, other.StreamSpan, comp);
    }

    public override bool Equals(object? obj) => obj is VfsPath other && Equals(other);

    /// <summary>
    /// Full 32-bit hash computed on demand from <see cref="PathSpan"/> + stream.
    /// The 36-bit partial hash stored in <c>_packed</c> is used only inside
    /// <see cref="Equals(VfsPath)"/> for a fast early-out; callers (dictionaries, hash sets)
    /// receive the full value here.
    /// </summary>
    public override int GetHashCode()
    {
        var comp = IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Hash value[start..queryOffset]: path + ":stream" when a stream is present.
        var s = Start;
        return string.GetHashCode(
            _value is null ? ReadOnlySpan<char>.Empty : _value.AsSpan(s, QueryOffset - s),
            comp);
    }

    public static bool operator ==(VfsPath left, VfsPath right) => left.Equals(right);
    public static bool operator !=(VfsPath left, VfsPath right) => !left.Equals(right);

    // Span-level equality that handles un-normalised dot segments without heap allocs.
    private static bool EqualsNormalized(
        ReadOnlySpan<char> aPath, ReadOnlySpan<char> aStream,
        ReadOnlySpan<char> bPath, ReadOnlySpan<char> bStream,
        StringComparison   comp)
    {
        if (!MemoryExtensions.Equals(aStream, bStream, comp)) return false;

        // Fast path: neither side has dot segments → compare directly.
        if (!ContainsDotSegment(aPath) && !ContainsDotSegment(bPath))
            return MemoryExtensions.Equals(aPath, bPath, comp);

        // Slow path: normalise both into stackalloc buffers, then compare.
        Span<char> aBuf = stackalloc char[MaxLength];
        Span<char> bBuf = stackalloc char[MaxLength];
        int aLen = NormalizeToSpan(aPath, aBuf);
        int bLen = NormalizeToSpan(bPath, bBuf);
        return MemoryExtensions.Equals(aBuf[..aLen], bBuf[..bLen], comp);
    }

    private static bool ContainsDotSegment(ReadOnlySpan<char> path)
    {
        for (int i = 0; i < path.Length - 1; i++)
            if (path[i] == '/' && path[i + 1] == '.') return true;
        return path.Length > 0 && path[0] == '.';
    }

    // -- Drive-prefix handling (Windows / named drives) ------------------------
    //
    // Turns a leading drive token into a leading path segment so the rest of the
    // system only ever sees a clean absolute path:
    //   "C:\"        -> "/c"
    //   "C:\a\b"     -> "/c/a/b"
    //   "azure:/x"   -> "/azure/x"
    //   "C:"         -> "/c"
    // The drive name is folded to lower-case so ToString() is canonical ("/c").
    //
    // Disambiguation from ADS streams ("report:thumb"): a drive prefix is
    // "<name>:" at position 0 where <name> is [A-Za-z][A-Za-z0-9]* and the colon
    // is followed by a separator OR end-of-input. A colon followed by anything
    // else stays an ADS stream separator and is left untouched.

    // Detects a leading drive prefix. On success, colonIndex is the ':' position.
    private static bool TryMatchDrivePrefix(ReadOnlySpan<char> raw, out int colonIndex)
    {
        colonIndex = -1;
        if (raw.Length < 2 || !char.IsAsciiLetter(raw[0])) return false;

        int i = 1;
        while (i < raw.Length && char.IsAsciiLetterOrDigit(raw[i])) i++;

        // Must land exactly on ':' followed by a separator or end-of-input.
        if (i >= raw.Length || raw[i] != ':') return false;
        int next = i + 1;
        if (next != raw.Length && raw[next] != '/' && raw[next] != '\\') return false;

        colonIndex = i;
        return true;
    }

    // Rewrites "<name>:<rest>" into "/<name><rest>" (colon dropped, leading slash
    // added - net-zero length). Folds the drive name to lower-case. Returns length.
    private static int RewriteDrivePrefix(ReadOnlySpan<char> raw, int colonIndex, Span<char> dest)
    {
        dest[0] = '/';
        for (int i = 0; i < colonIndex; i++)
            dest[1 + i] = char.ToLowerInvariant(raw[i]);
        raw[(colonIndex + 1)..].CopyTo(dest[(1 + colonIndex)..]);
        return raw.Length; // -1 colon, +1 slash
    }

    // True when a raw path string denotes an absolute location and must NOT be joined
    // with a base / current directory: leading separator, empty (→ root), or a drive
    // prefix ("C:\", "azure:/..."). A drive path is absolute, so the join overloads
    // must route it to direct normalisation rather than anchoring it under the CWD.
    private static bool StartsAbsolute(ReadOnlySpan<char> raw)
        => raw.IsEmpty || raw[0] == '/' || raw[0] == '\\' || TryMatchDrivePrefix(raw, out _);

    // -- Core normalisation ----------------------------------------------------

    // Normalises into a caller-supplied buffer. Returns chars written.
    // Used by the equality slow-path (stackalloc, no heap).
    private static int NormalizeToSpan(ReadOnlySpan<char> rawIn, Span<char> buf)
    {
        // Drive-prefix rewrite - must mirror NormalizeCore or equality would diverge.
        // Bound via a conditional so `raw` takes driveBuf's (narrow) ref-safe scope.
        Span<char> driveBuf = stackalloc char[MaxLength];
        ReadOnlySpan<char> raw = TryMatchDrivePrefix(rawIn, out int driveColon)
            ? driveBuf[..RewriteDrivePrefix(rawIn, driveColon, driveBuf)]
            : rawIn;

        bool isAbsolute = raw.Length > 0 && (raw[0] == '/' || raw[0] == '\\');
        bool firstSeg   = true;
        int  outLen     = 0;
        int  segStart   = -1;
        Span<int> segEnds = stackalloc int[64];
        int segCount = 0;

        for (int i = 0; i <= raw.Length; i++)
        {
            var c = i < raw.Length ? raw[i] : '/';
            if (c == '/' || c == '\\')
            {
                if (segStart >= 0)
                {
                    int segLen = i - segStart;
                    var seg    = raw.Slice(segStart, segLen);
                    if      (segLen == 1 && seg[0] == '.')
                        { /* skip */ }
                    else if (segLen == 2 && seg[0] == '.' && seg[1] == '.')
                    {
                        if (segCount > 0)
                        {
                            outLen = segCount > 1 ? segEnds[segCount - 2] : 0;
                            segCount--;
                        }
                    }
                    else
                    {
                        if (!firstSeg || isAbsolute) buf[outLen++] = '/';
                        seg.CopyTo(buf[outLen..]);
                        outLen += segLen;
                        segEnds[segCount++] = outLen;
                        firstSeg = false;
                    }
                    segStart = -1;
                }
            }
            else if (segStart < 0)
            {
                segStart = i;
            }
        }

        if (outLen == 0 && isAbsolute) buf[outLen++] = '/';
        return outLen;
    }

    // Joins a base span and a relative span, normalises, and returns a VfsPath.
    // Used by all From(base, relative) overloads.
    private static VfsPath CombineSpans(ReadOnlySpan<char> baseSpan, ReadOnlySpan<char> relative, bool caseSensitive)
    {
        var trimmedBase = baseSpan.TrimEnd('/');
        int totalLen = trimmedBase.Length + 1 + relative.Length;
        if (totalLen > MaxLength)
            throw new ArgumentException($"VFS path exceeds the {MaxLength}-character limit.");

        Span<char> buf = stackalloc char[MaxLength];
        trimmedBase.CopyTo(buf);
        buf[trimmedBase.Length] = '/';
        relative.CopyTo(buf[(trimmedBase.Length + 1)..]);

        var normalized = NormalizeCore(null, buf[..totalLen], out int so, out int qo);
        return new VfsPath(normalized, 0, so, qo, caseSensitive);
    }

    // Full normalisation: returns the ORIGINAL string reference for clean paths (zero alloc).
    // Also computes streamOffset and queryOffset into the result.
    private static string NormalizeCore(string? original, ReadOnlySpan<char> path,
        out int streamOffset, out int queryOffset)
    {
        // 1. Separate the query string - never normalise inside it.
        int qIdx  = path.IndexOf('?');
        var query = qIdx >= 0 ? path[qIdx..] : ReadOnlySpan<char>.Empty;  // includes '?'
        var rawIn = qIdx >= 0 ? path[..qIdx] : path;

        // 1b. Drive-prefix rewrite ("C:\" -> "/c"). Net-zero length. Must run before
        //     the segment loop so the drive colon is never seen by the ADS stream
        //     scan, and disqualifies the original-reference reuse below. Bound via a
        //     conditional so `raw` takes driveBuf's (narrow) ref-safe scope.
        Span<char> driveBuf = stackalloc char[MaxLength];
        bool isDrive = TryMatchDrivePrefix(rawIn, out int driveColon);
        ReadOnlySpan<char> raw = isDrive
            ? driveBuf[..RewriteDrivePrefix(rawIn, driveColon, driveBuf)]
            : rawIn;
        if (isDrive) original = null; // path changed - cannot return the caller's string

        // 2. Allocate output buffers.
        int maxLen = raw.Length + 1 + query.Length;
        char[]? rentedBuf = null;
        Span<char> buf = maxLen <= MaxLength
            ? stackalloc char[MaxLength]
            : (rentedBuf = ArrayPool<char>.Shared.Rent(maxLen)).AsSpan(0, maxLen);

        int maxSegs = (raw.Length >> 1) + 2;
        int[]? rentedSegs = null;
        Span<int> segEnds = maxSegs <= 64
            ? stackalloc int[64]
            : (rentedSegs = ArrayPool<int>.Shared.Rent(maxSegs)).AsSpan(0, maxSegs);

        try
        {
            bool isAbsolute = raw.Length > 0 && (raw[0] == '/' || raw[0] == '\\');
            bool firstSeg   = true;
            bool modified   = false;
            int  outLen     = 0;
            int  segStart   = -1;
            int  segCount   = 0;

            for (int i = 0; i <= raw.Length; i++)
            {
                var c     = i < raw.Length ? raw[i] : '/';
                var isSep = c == '/' || c == '\\';

                if (isSep)
                {
                    if (c == '\\') modified = true;

                    if (segStart >= 0)
                    {
                        int segLen = i - segStart;
                        var seg    = raw.Slice(segStart, segLen);

                        if (segLen == 1 && seg[0] == '.')
                        {
                            modified = true;  // '.' segment skipped
                        }
                        else if (segLen == 2 && seg[0] == '.' && seg[1] == '.')
                        {
                            modified = true;  // '..' segment popped
                            if (segCount > 0)
                            {
                                outLen = segCount > 1 ? segEnds[segCount - 2] : 0;
                                segCount--;
                            }
                            // '..' at relative root is a no-op (can't go above it without a base).
                        }
                        else
                        {
                            // Absolute paths always get a leading '/'.
                            // Relative paths get '/' only before segments after the first.
                            if (!firstSeg || isAbsolute) buf[outLen++] = '/';
                            seg.CopyTo(buf.Slice(outLen));
                            outLen += segLen;
                            segEnds[segCount++] = outLen;
                            firstSeg = false;
                        }

                        segStart = -1;
                    }
                }
                else if (segStart < 0)
                {
                    segStart = i;
                }
            }

            // Absolute paths with no segments (e.g. "/" or "/../..") clamp to root.
            // Relative paths that normalise to nothing stay empty ("." → "").
            if (outLen == 0 && isAbsolute) buf[outLen++] = '/';

            // Length mismatch catches all other modifications (consecutive slashes,
            // trailing slash, separators changed, segments added/removed, etc.).
            if (outLen != raw.Length) modified = true;

            // 3. Append query string unchanged.
            query.CopyTo(buf.Slice(outLen));
            int totalLen = outLen + query.Length;

            // 4. queryOffset = position of '?' (equals value.Length when no query).
            queryOffset = outLen;

            // 5. streamOffset = position of ':' in the last segment of the base path.
            //    Scan backwards for the last '/', then forward for ':'.
            int lastSlash = -1;
            for (int i = outLen - 1; i >= 0; i--)
                if (buf[i] == '/') { lastSlash = i; break; }

            int colonPos = -1;
            for (int i = lastSlash + 1; i < outLen; i++)
                if (buf[i] == ':') { colonPos = i; break; }

            streamOffset = colonPos >= 0 ? colonPos : queryOffset;

            // 6. Zero-alloc fast path: return original string reference when nothing changed.
            if (!modified && original is not null && original.Length == totalLen)
                return original;

            return new string(buf[..totalLen]);
        }
        finally
        {
            if (rentedSegs is not null) ArrayPool<int>.Shared.Return(rentedSegs);
            if (rentedBuf  is not null) ArrayPool<char>.Shared.Return(rentedBuf);
        }
    }
}
