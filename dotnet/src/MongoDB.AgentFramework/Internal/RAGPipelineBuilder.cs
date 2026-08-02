using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Internal;

/// <summary>
/// Builds the <c>$vectorSearch</c>-first aggregation pipeline shared by <see cref="MongoDBSearchMode.VectorAnn"/>
/// and <see cref="MongoDBSearchMode.VectorEnn"/>, per the pipeline pseudocode in
/// <c>docs/spec/features/rag.md</c>. The <c>$vectorSearch</c> stage itself is rendered from the typed
/// <see cref="PipelineStageDefinitionBuilder.VectorSearch{TInput}"/> builder, as required by the specification's
/// "typed MongoDB.Driver builders for supported stages" rule; the trailing score stage, which the driver has no
/// dedicated typed builder for in this context, is assembled directly as BSON. The pipeline intentionally does
/// <b>not</b> include a narrowing <c>$project</c> stage: <see cref="MongoDBRAGResult.RawDocument"/> must preserve
/// the complete original document, so the only field this pipeline adds beyond the original document is the
/// reserved <see cref="FieldPath.ReservedScoreAlias"/> score alias, which <c>MongoDBRAGProvider.MapResult</c> reads
/// and then strips before constructing the public result.
/// </summary>
internal static class RAGPipelineBuilder
{
    private static readonly RenderArgs<BsonDocument> RenderArgs = new(
        BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
        BsonSerializer.SerializerRegistry);

    /// <summary>
    /// Builds the complete ANN/ENN retrieval pipeline: <c>$vectorSearch</c> first, then a <c>$set</c> stage that
    /// captures MongoDB's native <c>vectorSearchScore</c> under the reserved
    /// <see cref="FieldPath.ReservedScoreAlias"/> alias. No stage narrows the document, so every field of the
    /// original document survives alongside the added score alias.
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
    public static BsonDocument[] BuildVectorSearchPipeline(
        string indexName,
        string vectorFieldName,
        float[] queryVector,
        int limit,
        bool exact,
        int? numCandidates,
        BsonDocument? filter)
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
            new BsonDocument(
                "$set",
                new BsonDocument(FieldPath.ReservedScoreAlias, new BsonDocument("$meta", "vectorSearchScore"))),
        ];
    }
}
