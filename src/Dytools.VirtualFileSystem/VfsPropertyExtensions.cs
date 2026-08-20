using System.Globalization;
using System.Text.Json;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Typed reads and writes over a string→string? property bag (<see cref="VfsNodeInfo"/>.Properties,
/// <see cref="VfsEntryInfo"/>.Properties, CatalogEntry.Properties). Storage stays portable strings; the
/// typing lives here at the edge. Scalars are parsed with the invariant culture; structured
/// values are JSON-encoded into the string by their producer and read back with <see cref="GetJson{T}"/>.
/// </summary>
public static class VfsPropertyExtensions
{
    // -- Reads (null-tolerant: missing key or unparseable value → null) --------

    /// <summary>
    /// Reads the raw string value for <paramref name="key"/>, or <c>null</c> when the bag is
    /// <c>null</c> or the key is absent.
    /// </summary>
    public static string? GetString(this IReadOnlyDictionary<string, string?>? props, string key)
        => props is not null && props.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Reads the value for <paramref name="key"/> as an <see cref="int"/>, or <c>null</c> when the
    /// key is missing or the value cannot be parsed.
    /// </summary>
    public static int? GetInt(this IReadOnlyDictionary<string, string?>? props, string key)
        => int.TryParse(props.GetString(key), NumberStyles.Integer, CultureInvariantOrNull, out var n) ? n : null;

    /// <summary>
    /// Reads the value for <paramref name="key"/> as a <see cref="long"/>, or <c>null</c> when the
    /// key is missing or the value cannot be parsed.
    /// </summary>
    public static long? GetLong(this IReadOnlyDictionary<string, string?>? props, string key)
        => long.TryParse(props.GetString(key), NumberStyles.Integer, CultureInvariantOrNull, out var n) ? n : null;

    /// <summary>
    /// Reads the value for <paramref name="key"/> as a <see cref="bool"/>, or <c>null</c> when the
    /// key is missing or the value cannot be parsed.
    /// </summary>
    public static bool? GetBool(this IReadOnlyDictionary<string, string?>? props, string key)
        => bool.TryParse(props.GetString(key), out var b) ? b : null;

    /// <summary>
    /// Reads the value for <paramref name="key"/> as a <see cref="DateTimeOffset"/> (round-trip
    /// kind), or <c>null</c> when the key is missing or the value cannot be parsed.
    /// </summary>
    public static DateTimeOffset? GetDateTimeOffset(this IReadOnlyDictionary<string, string?>? props, string key)
        => DateTimeOffset.TryParse(props.GetString(key), CultureInvariantOrNull,
                                   DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// <summary>
    /// Deserialize a value that was stored as JSON (see <see cref="PutJson{T}"/>). Returns
    /// <c>default(T)</c> when the key is absent, empty, or malformed.
    /// </summary>
    /// <typeparam name="T">Type to deserialize the stored JSON into.</typeparam>
    public static T? GetJson<T>(this IReadOnlyDictionary<string, string?>? props, string key)
    {
        var raw = props.GetString(key);
        if (string.IsNullOrEmpty(raw)) return default;
        try { return JsonSerializer.Deserialize<T>(raw); }
        catch (JsonException) { return default; }
    }

    // -- Writes (on a mutable bag a producer is building) ----------------------

    /// <summary>
    /// Writes <paramref name="value"/> into the bag using its invariant-culture string form.
    /// </summary>
    /// <typeparam name="T">A formattable value type.</typeparam>
    public static void Put<T>(this IDictionary<string, string?> props, string key, T value)
        where T : IFormattable
        => props[key] = value.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>Writes a string <paramref name="value"/> into the bag.</summary>
    public static void Put(this IDictionary<string, string?> props, string key, string? value)
        => props[key] = value;

    /// <summary>Writes a boolean <paramref name="value"/> into the bag as <c>"true"</c> or <c>"false"</c>.</summary>
    public static void Put(this IDictionary<string, string?> props, string key, bool value)
        => props[key] = value ? "true" : "false";

    /// <summary>
    /// Serialize a structured value into the bag as a JSON string. Read it back with
    /// <see cref="GetJson{T}"/>.
    /// </summary>
    /// <typeparam name="T">Type of the value being serialized.</typeparam>
    public static void PutJson<T>(this IDictionary<string, string?> props, string key, T value)
        => props[key] = JsonSerializer.Serialize(value);

    private static readonly CultureInfo CultureInvariantOrNull = CultureInfo.InvariantCulture;
}
