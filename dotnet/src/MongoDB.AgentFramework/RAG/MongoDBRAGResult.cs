using MongoDB.AgentFramework.Internal;
using MongoDB.Bson;

namespace MongoDB.AgentFramework;

/// <summary>
/// An immutable, normalized MongoDB RAG retrieval result that preserves the raw retrieved document for advanced
/// callers while giving the framework a stable, mode-independent shape to build attributed context or citations
/// from.
/// </summary>
public sealed record MongoDBRAGResult
{
    private readonly BsonDocument _rawDocument;

    /// <summary>Initializes an immutable, normalized RAG result.</summary>
    /// <param name="id">The document identifier mapped from the configured ID field.</param>
    /// <param name="text">The retrieved chunk text mapped from the configured text field.</param>
    /// <param name="score">The MongoDB-native vector, search, or fused rank score.</param>
    /// <param name="sourceName">The optional attributed source title or name.</param>
    /// <param name="sourceUrl">The optional attributed source URL.</param>
    /// <param name="metadata">Optional, defensively deep-cloned metadata values.</param>
    /// <param name="rawDocument">
    /// The raw retrieved document. A defensive deep clone is stored so later mutation of the caller's document
    /// cannot change this instance after construction; <see cref="RawDocument"/> in turn returns a fresh deep-clone
    /// snapshot on every access so a caller mutating a previously returned document cannot change this instance or
    /// any subsequent read either.
    /// </param>
    public MongoDBRAGResult(
        string id,
        string text,
        double score,
        string? sourceName = null,
        string? sourceUrl = null,
        IReadOnlyDictionary<string, BsonValue>? metadata = null,
        BsonDocument? rawDocument = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new MongoDBConfigurationException("id must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(text);

        Id = id;
        Text = text;
        Score = score;
        SourceName = sourceName;
        SourceUrl = sourceUrl;
        Metadata = metadata is null ? ImmutableBsonMetadata.Empty : ImmutableBsonMetadata.CopyFrom(metadata);
        _rawDocument = rawDocument is null ? new BsonDocument() : (BsonDocument)rawDocument.DeepClone();
    }

    /// <summary>Gets the document identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the retrieved chunk text.</summary>
    public string Text { get; }

    /// <summary>Gets the MongoDB-native score; comparable only within the same mode and query.</summary>
    public double Score { get; }

    /// <summary>Gets the optional attributed source title or name.</summary>
    public string? SourceName { get; }

    /// <summary>Gets the optional attributed source URL.</summary>
    public string? SourceUrl { get; }

    /// <summary>
    /// Gets optional normalized metadata values. Every read returns deep-cloned <see cref="BsonValue"/> instances,
    /// so a caller cannot mutate a nested <see cref="BsonDocument"/> or <see cref="BsonArray"/> value to affect this
    /// result or any other read.
    /// </summary>
    public IReadOnlyDictionary<string, BsonValue> Metadata { get; }

    /// <summary>
    /// Gets a fresh deep-clone snapshot of the raw retrieved document, preserved for advanced callers. Each access
    /// returns an independent copy, so mutating a previously returned document has no effect on this result or on
    /// any subsequently returned snapshot.
    /// </summary>
    public BsonDocument RawDocument => (BsonDocument)_rawDocument.DeepClone();
}
