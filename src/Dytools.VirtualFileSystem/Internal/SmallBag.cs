using System.Collections;

namespace Dytools.VirtualFileSystem.Internal;

// Lightweight IDictionary<string, object> for VfsContext.Items.
// Optimised for the common case of 1-4 items with ordinal string keys.
//
// Backing store: a flat KeyValuePair array - linear scan, no hash table.
// For small N (typical middleware use: identity, cache hints, flags) this is
// faster than a Dictionary and allocates roughly half as much.
//
// Allocation when first written:
//   SmallBag object:  ~32B  (vs Dictionary ~72B)
//   Entry array cap 4: ~80B  (vs buckets ~32B + entries ~112B)
//   Total: ~112B  (vs ~200B for Dictionary)
//
// If items exceed InitialCapacity the array doubles. No hard cap - growth
// is rare (middleware chains are short), so no Dictionary fallback needed.
internal sealed class SmallBag : IDictionary<string, object>
{
    private const int InitialCapacity = 4;

    private KeyValuePair<string, object>[] _entries;
    private int _count;

    internal SmallBag() => _entries = new KeyValuePair<string, object>[InitialCapacity];

    // -- Core operations -------------------------------------------------------

    public object this[string key]
    {
        get
        {
            var i = IndexOf(key);
            if (i < 0) throw new KeyNotFoundException($"Key not found: {key}");
            return _entries[i].Value;
        }
        set
        {
            var i = IndexOf(key);
            if (i >= 0) { _entries[i] = new KeyValuePair<string, object>(key, value); return; }
            EnsureCapacity();
            _entries[_count++] = new KeyValuePair<string, object>(key, value);
        }
    }

    public bool TryGetValue(string key, out object value)
    {
        var i = IndexOf(key);
        if (i < 0) { value = null!; return false; }
        value = _entries[i].Value;
        return true;
    }

    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    public void Add(string key, object value)
    {
        if (IndexOf(key) >= 0) throw new ArgumentException($"Key already exists: {key}");
        EnsureCapacity();
        _entries[_count++] = new KeyValuePair<string, object>(key, value);
    }

    public bool Remove(string key)
    {
        var i = IndexOf(key);
        if (i < 0) return false;
        // Swap last entry into the gap - O(1), order not guaranteed.
        _entries[i] = _entries[--_count];
        _entries[_count] = default;
        return true;
    }

    public void Clear()
    {
        Array.Clear(_entries, 0, _count);
        _count = 0;
    }

    public int Count => _count;

    // -- IEnumerable -----------------------------------------------------------

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return _entries[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // -- ICollection<KeyValuePair<string,object>> ------------------------------

    bool ICollection<KeyValuePair<string, object>>.IsReadOnly => false;

    void ICollection<KeyValuePair<string, object>>.Add(KeyValuePair<string, object> item)
        => Add(item.Key, item.Value);

    bool ICollection<KeyValuePair<string, object>>.Contains(KeyValuePair<string, object> item)
    {
        var i = IndexOf(item.Key);
        return i >= 0 && Equals(_entries[i].Value, item.Value);
    }

    bool ICollection<KeyValuePair<string, object>>.Remove(KeyValuePair<string, object> item)
    {
        var i = IndexOf(item.Key);
        if (i < 0 || !Equals(_entries[i].Value, item.Value)) return false;
        _entries[i] = _entries[--_count];
        _entries[_count] = default;
        return true;
    }

    void ICollection<KeyValuePair<string, object>>.CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
    {
        for (int i = 0; i < _count; i++)
            array[arrayIndex + i] = _entries[i];
    }

    // -- IDictionary keys/values (allocate on access - not used in hot paths) --

    public ICollection<string> Keys
    {
        get
        {
            var keys = new string[_count];
            for (int i = 0; i < _count; i++) keys[i] = _entries[i].Key;
            return keys;
        }
    }

    public ICollection<object> Values
    {
        get
        {
            var values = new object[_count];
            for (int i = 0; i < _count; i++) values[i] = _entries[i].Value;
            return values;
        }
    }

    // -- Private helpers -------------------------------------------------------

    private int IndexOf(string key)
    {
        for (int i = 0; i < _count; i++)
            if (string.Equals(_entries[i].Key, key, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private void EnsureCapacity()
    {
        if (_count < _entries.Length) return;
        var grown = new KeyValuePair<string, object>[_entries.Length * 2];
        _entries.AsSpan(0, _count).CopyTo(grown);
        _entries = grown;
    }
}
