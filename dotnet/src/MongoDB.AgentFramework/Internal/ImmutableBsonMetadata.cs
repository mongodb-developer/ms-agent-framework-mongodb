using System.Collections;
using MongoDB.Bson;

namespace MongoDB.AgentFramework.Internal;

/// <summary>
/// A read-only, deep-clone-on-read view over a set of <see cref="BsonValue"/> metadata values. <see cref="BsonDocument"/>
/// and <see cref="BsonArray"/> are mutable reference types, so returning a stored value directly from an indexer or
/// enumerator would let a caller mutate state the owning immutable result already promised was frozen. Every read
/// path (indexer, <see cref="TryGetValue"/>, and enumeration) therefore returns an independent
/// <see cref="BsonValue.DeepClone"/> snapshot, in addition to the values already having been deep-cloned once at
/// construction so a later mutation of the caller's original source dictionary/values cannot reach this instance
/// either.
/// </summary>
internal sealed class ImmutableBsonMetadata : IReadOnlyDictionary<string, BsonValue>, IDictionary<string, BsonValue>
{
    private static readonly ImmutableBsonMetadata EmptyInstance =
        new(new Dictionary<string, BsonValue>(StringComparer.Ordinal));

    private readonly Dictionary<string, BsonValue> _values;

    private ImmutableBsonMetadata(Dictionary<string, BsonValue> values)
    {
        _values = values;
    }

    /// <summary>Gets a shared, empty instance.</summary>
    internal static ImmutableBsonMetadata Empty => EmptyInstance;

    /// <summary>Creates an instance whose values are deep-cloned from <paramref name="source"/> at construction.</summary>
    internal static ImmutableBsonMetadata CopyFrom(IReadOnlyDictionary<string, BsonValue> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var values = new Dictionary<string, BsonValue>(source.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, BsonValue> pair in source)
        {
            values[pair.Key] = Clone(pair.Value);
        }

        return new ImmutableBsonMetadata(values);
    }

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<BsonValue> Values => _values.Values.Select(Clone);

    bool ICollection<KeyValuePair<string, BsonValue>>.IsReadOnly => true;

    ICollection<string> IDictionary<string, BsonValue>.Keys => _values.Keys;

    ICollection<BsonValue> IDictionary<string, BsonValue>.Values => Values.ToList();

    public BsonValue this[string key]
    {
        get => Clone(_values[key]);
        set => throw ReadOnly();
    }

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out BsonValue value)
    {
        if (_values.TryGetValue(key, out BsonValue? stored))
        {
            value = Clone(stored);
            return true;
        }

        value = null!;
        return false;
    }

    public IEnumerator<KeyValuePair<string, BsonValue>> GetEnumerator()
    {
        foreach (KeyValuePair<string, BsonValue> pair in _values)
        {
            yield return new KeyValuePair<string, BsonValue>(pair.Key, Clone(pair.Value));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void IDictionary<string, BsonValue>.Add(string key, BsonValue value) => throw ReadOnly();

    bool IDictionary<string, BsonValue>.Remove(string key) => throw ReadOnly();

    void ICollection<KeyValuePair<string, BsonValue>>.Add(KeyValuePair<string, BsonValue> item) => throw ReadOnly();

    void ICollection<KeyValuePair<string, BsonValue>>.Clear() => throw ReadOnly();

    bool ICollection<KeyValuePair<string, BsonValue>>.Contains(KeyValuePair<string, BsonValue> item) =>
        _values.TryGetValue(item.Key, out BsonValue? value) && value.Equals(item.Value);

    void ICollection<KeyValuePair<string, BsonValue>>.CopyTo(KeyValuePair<string, BsonValue>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (KeyValuePair<string, BsonValue> pair in this)
        {
            array[arrayIndex++] = pair;
        }
    }

    bool ICollection<KeyValuePair<string, BsonValue>>.Remove(KeyValuePair<string, BsonValue> item) => throw ReadOnly();

    private static BsonValue Clone(BsonValue value) => (BsonValue)value.DeepClone();

    private static NotSupportedException ReadOnly() => new("Metadata is read-only.");
}
