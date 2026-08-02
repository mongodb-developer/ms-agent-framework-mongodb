using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Internal;

/// <summary>
/// Builds the <c>$vectorSearch</c>-first aggregation pipeline shared by <see cref="MongoDBSearchMode.VectorAnn"/>
/// and <see cref="MongoDBSearchMode.VectorEnn"/>, per the pipeline pseudocode in
/// <c>docs/spec/features/rag.md</c>. The <c>$vectorSearch</c> stage itself is rendered from the typed
/// <see cref="PipelineStageDefinitionBuilder.VectorSearch{TInput}"/> builder, as required by the specification's
/// "typed MongoDB.Driver builders for supported stages" rule; only the trailing score/projection stages, which the
/// driver has no dedicated typed builder for in this context, are assembled directly as BSON.
/// </summary>
internal static class RAGPipelineBuilder
{
    private static readonly RenderArgs<BsonDocument> RenderArgs = new(
        BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
        BsonSerializer.SerializerRegistry);

    /// <summary>
    /// Builds the complete ANN/ENN retrieval pipeline: <c>$vectorSearch</c> first, a <c>$set</c> stage that
    /// captures MongoDB's native <c>vectorSearchScore</c> under the reserved <c>_ragScore</c> alias, and a final
    /// <c>$project</c> stage that narrows the result to the caller-supplied <paramref name="projection"/>.
    /// </summary>
    /// <param name="indexName">The configured Vector Search index name.</param>
    /// <param name="vectorFieldName">The configured embedding field path.</param>
    /// <param name="queryVector">The embedded query vector.</param>
    /// <param name="limit">The final result limit (<c>topK</c>).</param>
    /// <param name="exact">
    /// <see langword="true"/> for <see cref="MongoDBSearchMode.VectorEnn"/> exact search; <see langword="false"/>
    /// for <see cref="MongoDBSearchMode.VectorAnn"/> approximate search.
    /// </param>
    /// <param name="numCandidates">
    /// The ANN candidate count. Must be <see langword="null"/> when <paramref name="exact"/> is
    /// <see langword="true"/>; the two are mutually exclusive per the search-mode option contract.
    /// </param>
    /// <param name="filter">
    /// The translated <c>$vectorSearch.filter</c> match document, or <see langword="null"/> to omit the property
    /// entirely when there is no effective mandatory filter.
    /// </param>
    /// <param name="projection">The <c>$project</c> stage's mapped result fields.</param>
    public static BsonDocument[] BuildVectorSearchPipeline(
        string indexName,
        string vectorFieldName,
        float[] queryVector,
        int limit,
        bool exact,
        int? numCandidates,
        BsonDocument? filter,
        BsonDocument projection)
    {
        if (exact && numCandidates is not null)
        {
            throw new MongoDBConfigurationException(
                "numCandidates must not be set when exact search is requested.");
        }

        var options = new VectorSearchOptions<BsonDocument>
        {
            IndexName = indexName,
            Exact = exact,
            NumberOfCandidates = exact ? null : numCandidates,
            Filter = filter is null ? null : new BsonDocumentFilterDefinition<BsonDocument>(filter),
        };
        PipelineStageDefinition<BsonDocument, BsonDocument> vectorSearchStage =
            PipelineStageDefinitionBuilder.VectorSearch<BsonDocument>(
                new StringFieldDefinition<BsonDocument>(vectorFieldName),
                new QueryVector(queryVector),
                limit,
                options);

        return
        [
            vectorSearchStage.Render(RenderArgs).Document,
            new BsonDocument("$set", new BsonDocument("_ragScore", new BsonDocument("$meta", "vectorSearchScore"))),
            new BsonDocument("$project", projection),
        ];
    }

    /// <summary>
    /// Builds the <c>$project</c> stage's mapped result fields from the configured RAG field mappings: the
    /// document identifier, chunk text, optional source name/URL, and optional metadata fields, plus the reserved
    /// <c>_ragScore</c> alias. Duplicate field paths (for example a metadata field that repeats the source-name
    /// field) contribute a single projection entry.
    /// </summary>
    public static BsonDocument BuildProjection(MongoDBRAGProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var projection = new BsonDocument();
        Include(projection, options.IdFieldName);
        Include(projection, options.ChunkTextFieldName);
        if (options.SourceNameFieldName is { } sourceName)
        {
            Include(projection, sourceName);
        }

        if (options.SourceUrlFieldName is { } sourceUrl)
        {
            Include(projection, sourceUrl);
        }

        if (options.MetadataFieldNames is { } metadataFieldNames)
        {
            foreach (string field in metadataFieldNames)
            {
                Include(projection, field);
            }
        }

        Include(projection, "_ragScore");
        return projection;
    }

    private static void Include(BsonDocument projection, string path)
    {
        if (!projection.Contains(path))
        {
            projection.Add(path, 1);
        }
    }
}
