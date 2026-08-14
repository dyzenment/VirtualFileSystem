using System.Globalization;
using System.Text.Json;

namespace Dytools.VirtualFileSystem;

// Typed reads and writes over a string→string? property bag (VfsNodeInfo.Properties,
// VfsEntryInfo.Properties, CatalogEntry.Properties). Storage stays portable strings; the
// typing lives here at the edge. Scalars are parsed with the invariant culture; structured
// values are JSON-encoded into the string by their producer and read back with GetJson.
public static class VfsPropertyExtensions
{
    // -- Reads (null-tolerant: missing key or unparseable value → null) --------

    public static string? GetString(this IReadOnlyDictionary<string, string?>? props, string key)
        => props is not null && props.TryGetValue(key, out var v) ? v : null;

    public static int? GetInt(this IReadOnlyDictionary<string, string?>? props, string key)
        => int.TryParse(props.GetString(key), NumberStyles.Integer, CultureInvariantOrNull, out var n) ? n : null;

    public static long? GetLong(this IReadOnlyDictionary<string, string?>? props, string key)
        => long.TryParse(props.GetString(key), NumberStyles.Integer, CultureInvariantOrNull, out var n) ? n : null;

    public static bool? GetBool(this IReadOnlyDictionary<string, string?>? props, string key)
        => bool.TryParse(props.GetString(key), out var b) ? b : null;

    public static DateTimeOffset? GetDateTimeOffset(this IReadOnlyDictionary<string, string?>? props, string key)
        => DateTimeOffset.TryParse(props.GetString(key), CultureInvariantOrNull,
                                   DateTimeStyles.RoundtripKind, out var d) ? d : null;

    // Deserialize a value that was stored as JSON (see PutJson). Returns default(T) when the
    // key is absent, empty, or malformed.
    public static T? GetJson<T>(this IReadOnlyDictionary<string, string?>? props, string key)
    {
        var raw = props.GetString(key);
        if (string.IsNullOrEmpty(raw)) return default;
        try { return JsonSerializer.Deserialize<T>(raw); }
        catch (JsonException) { return default; }
    }

    // -- Writes (on a mutable bag a producer is building) ----------------------

    public static void Put<T>(this IDictionary<string, string?> props, string key, T value)
        where T : IFormattable
        => props[key] = value.ToString(null, CultureInfo.InvariantCulture);

    public static void Put(this IDictionary<string, string?> props, string key, string? value)
        => props[key] = value;

    public static void Put(this IDictionary<string, string?> props, string key, bool value)
        => props[key] = value ? "true" : "false";

    // Serialize a structured value into the bag as a JSON string. Read it back with GetJson.
    public static void PutJson<T>(this IDictionary<string, string?> props, string key, T value)
        => props[key] = JsonSerializer.Serialize(value);

    private static readonly CultureInfo CultureInvariantOrNull = CultureInfo.InvariantCulture;
}
