using MongoDB.AgentFramework.Internal;

namespace MongoDB.AgentFramework;

/// <summary>
/// An immutable, structured Vector Search index definition: the indexed vector field's path, dimensions, and
/// similarity function, plus every field path that must be independently declared as a Vector Search
/// <c>type: "filter"</c> field. Used both for Memory's fixed scope-filter definition and for RAG's Vector Search
/// (and Hybrid's vector branch) definition, where every <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>
/// field reference must appear in <see cref="FilterFieldPaths"/> (docs/spec/features/index-management.md and
/// rag.md's field-path validation requirement).
/// </summary>
public sealed record MongoDBVectorSearchIndexDefinition
{
    /// <summary>Initializes an immutable Vector Search index definition.</summary>
    /// <param name="indexName">The Vector Search index name.</param>
    /// <param name="vectorFieldName">The indexed embedding field path.</param>
    /// <param name="vectorDimensions">The indexed vector dimension count. Must be positive.</param>
    /// <param name="similarity">
    /// The indexed similarity function (<c>cosine</c>, <c>dotProduct</c>, or <c>euclidean</c>), or
    /// <see langword="null"/> to skip similarity comparison (used by callers, such as Hybrid's <c>$rankFusion</c>,
    /// for which a mismatched similarity metric does not break correctness the way it would for a raw-score-based
    /// caller).
    /// </param>
    /// <param name="filterFieldPaths">
    /// Field paths that must be independently declared as Vector Search <c>type: "filter"</c> fields. Defaults to
    /// none.
    /// </param>
    public MongoDBVectorSearchIndexDefinition(
        string indexName,
        string vectorFieldName,
        int vectorDimensions,
        string? similarity = null,
        IReadOnlyList<string>? filterFieldPaths = null)
    {
        IndexName = Internal.IndexName.Validate(indexName, nameof(indexName));
        VectorFieldName = FieldPath.Validate(vectorFieldName, nameof(vectorFieldName));
        VectorDimensions = EmbeddingValidator.ValidateDimensions(vectorDimensions);
        if (similarity is not (null or "cosine" or "dotProduct" or "euclidean"))
        {
            throw new MongoDBConfigurationException(
                $"{nameof(similarity)} must be cosine, dotProduct, euclidean, or null.");
        }

        Similarity = similarity;
        foreach (string path in filterFieldPaths ?? [])
        {
            FieldPath.Validate(path, nameof(filterFieldPaths));
        }

        FilterFieldPaths = ImmutableCollections.Snapshot(filterFieldPaths);
    }

    /// <summary>Gets the Vector Search index name.</summary>
    public string IndexName { get; }

    /// <summary>Gets the indexed embedding field path.</summary>
    public string VectorFieldName { get; }

    /// <summary>Gets the indexed vector dimension count.</summary>
    public int VectorDimensions { get; }

    /// <summary>
    /// Gets the indexed similarity function, or <see langword="null"/> when similarity is not compared.
    /// </summary>
    public string? Similarity { get; }

    /// <summary>Gets the field paths that must be declared as Vector Search <c>type: "filter"</c> fields.</summary>
    public IReadOnlyList<string> FilterFieldPaths { get; }
}
