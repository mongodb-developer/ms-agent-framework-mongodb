using System.Collections.ObjectModel;
using MongoDB.Bson;

namespace MongoDB.AgentFramework;

/// <summary>
/// An immutable, normalized MongoDB RAG retrieval result that preserves the raw retrieved document for advanced
/// callers while giving the framework a stable, mode-independent shape to build attributed context or citations
/// from.
/// </summary>
public sealed record MongoDBRAGResult
{
    private static readonly ReadOnlyDictionary<string, BsonValue> EmptyMetadata =
        new(new Dictionary<string, BsonValue>(StringComparer.Ordinal));

    /// <summary>Initializes an immutable, normalized RAG result.</summary>
    /// <param name="id">The document identifier mapped from the configured ID field.</param>
    /// <param name="text">The retrieved chunk text mapped from the configured text field.</param>
    /// <param name="score">The MongoDB-native vector, search, or fused rank score.</param>
    /// <param name="sourceName">The optional attributed source title or name.</param>
    /// <param name="sourceUrl">The optional attributed source URL.</param>
    /// <param name="metadata">Optional, defensively copied metadata values.</param>
    /// <param name="rawDocument">
    /// The raw retrieved document. A defensive deep clone is stored so later mutation of the caller's document, or
    /// of the result's own copy, cannot change this instance after construction.
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
        Metadata = metadata is null
            ? EmptyMetadata
            : new ReadOnlyDictionary<string, BsonValue>(new Dictionary<string, BsonValue>(metadata, StringComparer.Ordinal));
        RawDocument = rawDocument is null
            ? new BsonDocument()
            : (BsonDocument)rawDocument.DeepClone();
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

    /// <summary>Gets optional normalized metadata values.</summary>
    public IReadOnlyDictionary<string, BsonValue> Metadata { get; }

    /// <summary>Gets a snapshot of the raw retrieved document, preserved for advanced callers.</summary>
    public BsonDocument RawDocument { get; }
}
