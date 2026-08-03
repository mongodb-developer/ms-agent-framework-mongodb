using MongoDB.Bson;

namespace MongoDB.AgentFramework;

/// <summary>
/// An immutable, inspected snapshot of a MongoDB Search or Vector Search index, returned by
/// <see cref="MongoDBMemoryIndexManager"/>/<see cref="MongoDBRAGIndexManager"/>'s read-only inspection methods
/// (<c>GetIndexAsync</c>/<c>ListIndexesAsync</c>) and by <c>EnsureIndexAsync</c>/<c>WaitUntilReadyAsync</c> on
/// success. Retrieval methods that return this type never mutate MongoDB.
/// </summary>
public sealed record MongoDBIndexInfo
{
    private readonly BsonDocument _rawDefinition;

    /// <summary>Initializes an immutable inspected index snapshot.</summary>
    /// <param name="name">The index name.</param>
    /// <param name="type">The MongoDB-reported index type (for example <c>"vectorSearch"</c> or <c>"search"</c>).</param>
    /// <param name="status">The classified lifecycle status.</param>
    /// <param name="queryable">Whether the index currently reports <c>queryable: true</c>.</param>
    /// <param name="rawStatus">The raw, MongoDB-reported status string (for example <c>"READY"</c>, <c>"PENDING"</c>).</param>
    /// <param name="rawDefinition">
    /// The raw index definition document. A defensive deep clone is stored so later mutation of the caller's
    /// document cannot change this instance after construction; <see cref="RawDefinition"/> in turn returns a
    /// fresh deep-clone snapshot on every access.
    /// </param>
    public MongoDBIndexInfo(
        string name,
        string type,
        MongoDBIndexStatus status,
        bool queryable,
        string rawStatus,
        BsonDocument? rawDefinition = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MongoDBConfigurationException("name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            throw new MongoDBConfigurationException("type must not be empty.");
        }

        Name = name;
        Type = type;
        Status = status;
        Queryable = queryable;
        RawStatus = rawStatus ?? string.Empty;
        _rawDefinition = rawDefinition is null ? new BsonDocument() : (BsonDocument)rawDefinition.DeepClone();
    }

    /// <summary>Gets the index name.</summary>
    public string Name { get; }

    /// <summary>Gets the MongoDB-reported index type (for example <c>"vectorSearch"</c> or <c>"search"</c>).</summary>
    public string Type { get; }

    /// <summary>Gets the classified lifecycle status.</summary>
    public MongoDBIndexStatus Status { get; }

    /// <summary>Gets whether the index currently reports <c>queryable: true</c>.</summary>
    public bool Queryable { get; }

    /// <summary>Gets the raw, MongoDB-reported status string.</summary>
    public string RawStatus { get; }

    /// <summary>
    /// Gets a fresh deep-clone snapshot of the raw index definition document, preserved for advanced callers.
    /// Each access returns an independent copy, so mutating a previously returned document has no effect on this
    /// instance or on any subsequently returned snapshot.
    /// </summary>
    public BsonDocument RawDefinition => (BsonDocument)_rawDefinition.DeepClone();
}
