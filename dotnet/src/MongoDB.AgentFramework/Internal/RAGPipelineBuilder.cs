using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;

namespace MongoDB.AgentFramework.Internal;

/// <summary>
/// Builds the <c>$vectorSearch</c>-first, <c>$search</c>-first, and <c>$rankFusion</c>-first aggregation pipelines
/// for <see cref="MongoDBSearchMode.VectorAnn"/>/<see cref="MongoDBSearchMode.VectorEnn"/>,
/// <see cref="MongoDBSearchMode.FullText"/>, and <see cref="MongoDBSearchMode.HybridRrf"/> respectively, per the
/// pipeline pseudocode in <c>docs/spec/features/rag.md</c>. Each stage envelope is rendered from a typed
/// <c>MongoDB.Driver</c> builder (<see cref="PipelineStageDefinitionBuilder.VectorSearch{TInput}"/>,
/// <see cref="PipelineStageDefinitionBuilder.Search{TInput}(SearchDefinition{TInput}, SearchOptions{TInput})"/>, or
/// <see cref="PipelineStageDefinitionBuilder.RankFusion{TInput,TOutput}(Dictionary{string,PipelineDefinition{TInput,TOutput}},Dictionary{string,double},RankFusionOptions{TOutput})"/>),
/// as required by the specification's
/// "typed MongoDB.Driver builders for supported stages" rule; the <c>compound</c> filter body a mandatory filter
/// translates to has no dedicated typed sub-builder, so it is wrapped as a
/// <see cref="BsonDocumentSearchDefinition{TDocument}"/>, and the trailing score stages, which the driver has no
/// dedicated typed builder for in this context, are assembled directly as BSON. No pipeline includes a narrowing
/// <c>$project</c> stage: <see cref="MongoDBRAGResult.RawDocument"/> must preserve the complete original document,
/// so the only fields any pipeline adds beyond the original document are the reserved
/// <see cref="FieldPath.ReservedScoreAlias"/> score alias and, for Hybrid RRF when explicitly requested, the
/// reserved <see cref="FieldPath.ReservedScoreDetailsAlias"/> diagnostic alias; <c>MongoDBRAGProvider.MapResult</c>
/// reads and then strips both before constructing the public result.
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
        BsonDocument? filter) =>
        [
            BuildVectorSearchStage(indexName, vectorFieldName, queryVector, limit, exact, numCandidates, filter),
            ScoreAliasStage("vectorSearchScore"),
        ];

    /// <summary>
    /// Builds the complete <see cref="MongoDBSearchMode.FullText"/> retrieval pipeline: <c>$search</c> first (a
    /// single <c>compound.must</c> text clause against <paramref name="textFieldNames"/>, plus the translated
    /// mandatory filter placed inside <c>compound.filter</c> so authorization narrows the candidate set MongoDB
    /// Search itself scores, not a post-hoc application-side filter), then <c>$limit</c> to
    /// <paramref name="limit"/> (topK), then a <c>$set</c> stage capturing MongoDB's native
    /// <c>{ $meta: "searchScore" }</c> under the reserved <see cref="FieldPath.ReservedScoreAlias"/> alias. Like
    /// <see cref="BuildVectorSearchPipeline"/>, no stage narrows the document, so
    /// <see cref="MongoDBRAGResult.RawDocument"/> preserves the complete original document alongside the added
    /// score alias.
    /// </summary>
    /// <param name="indexName">The configured Search index name.</param>
    /// <param name="textFieldNames">
    /// The configured full-text field paths. A single entry renders as a scalar <c>path</c>; more than one renders
    /// as an array, matching the <c>$search</c> <c>text</c> operator's own scalar/array duality.
    /// </param>
    /// <param name="queryText">The natural-language query text.</param>
    /// <param name="limit">The final result limit (<c>topK</c>).</param>
    /// <param name="filter">
    /// The translated <c>compound.filter</c> array, or <see langword="null"/> to omit the property entirely when
    /// there is no effective mandatory filter.
    /// </param>
    public static BsonDocument[] BuildFullTextSearchPipeline(
        string indexName,
        IReadOnlyList<string> textFieldNames,
        string queryText,
        int limit,
        BsonArray? filter) =>
        [
            BuildFullTextSearchStage(indexName, textFieldNames, queryText, filter),
            new BsonDocument("$limit", limit),
            ScoreAliasStage("searchScore"),
        ];

    /// <summary>
    /// Builds the complete <see cref="MongoDBSearchMode.HybridRrf"/> retrieval pipeline: native <c>$rankFusion</c>
    /// first (a same-collection <c>vector</c> input running <c>$vectorSearch</c> ANN only, and a <c>text</c> input
    /// running <c>$search</c> followed by a candidate <c>$limit</c>, each with its own independently translated
    /// mandatory filter placed inside its own retrieval stage), then the final <c>$limit</c> to
    /// <paramref name="limit"/> (topK), then a <c>$set</c> stage capturing <c>$rankFusion</c>'s native
    /// <c>{ $meta: "score" }</c> fused rank score under the reserved <see cref="FieldPath.ReservedScoreAlias"/>
    /// alias, and, only when <paramref name="includeScoreDetails"/> is <see langword="true"/>, a further
    /// <c>$set</c> stage capturing <c>{ $meta: "scoreDetails" }</c> under the reserved
    /// <see cref="FieldPath.ReservedScoreDetailsAlias"/> alias (rag.md: "not a compatibility guarantee"). Per
    /// rag.md's hybrid rules, no stage after <c>$rankFusion</c> ever filters again, de-duplication is left entirely
    /// to <c>$rankFusion</c>'s own semantics, and — like every other mode's pipeline — no stage narrows the
    /// document (no <c>$project</c>), so <see cref="MongoDBRAGResult.RawDocument"/> preserves the complete original
    /// document alongside the added alias(es).
    /// </summary>
    /// <param name="vectorIndexName">The configured Vector Search index name.</param>
    /// <param name="vectorFieldName">The configured embedding field path.</param>
    /// <param name="queryVector">The embedded query vector.</param>
    /// <param name="vectorNumCandidates">The vector input's ANN candidate count (<c>$vectorSearch.numCandidates</c>).</param>
    /// <param name="vectorCandidateLimit">
    /// The vector input's own <c>$vectorSearch.limit</c>: the candidate count handed to <c>$rankFusion</c>,
    /// distinct from the final <paramref name="limit"/> (topK).
    /// </param>
    /// <param name="vectorFilter">
    /// The translated <c>$vectorSearch.filter</c> match document for the vector input, or <see langword="null"/> to
    /// omit the property entirely when there is no effective mandatory filter.
    /// </param>
    /// <param name="searchIndexName">The configured Search index name.</param>
    /// <param name="textFieldNames">The configured full-text field paths (see <see cref="BuildFullTextSearchPipeline"/>).</param>
    /// <param name="queryText">The natural-language query text.</param>
    /// <param name="textCandidateLimit">
    /// The text input's own trailing <c>$limit</c>: the candidate count handed to <c>$rankFusion</c>, distinct from
    /// the final <paramref name="limit"/> (topK).
    /// </param>
    /// <param name="searchFilter">
    /// The translated <c>compound.filter</c> array for the text input, or <see langword="null"/> to omit the
    /// property entirely when there is no effective mandatory filter.
    /// </param>
    /// <param name="vectorWeight">The <c>combination.weights.vector</c> fusion weight.</param>
    /// <param name="textWeight">The <c>combination.weights.text</c> fusion weight.</param>
    /// <param name="includeScoreDetails">
    /// Whether to request and capture <c>$rankFusion</c>'s <c>scoreDetails</c> diagnostic metadata.
    /// </param>
    /// <param name="limit">The final result limit (<c>topK</c>).</param>
    public static BsonDocument[] BuildHybridRankFusionPipeline(
        string vectorIndexName,
        string vectorFieldName,
        float[] queryVector,
        int vectorNumCandidates,
        int vectorCandidateLimit,
        BsonDocument? vectorFilter,
        string searchIndexName,
        IReadOnlyList<string> textFieldNames,
        string queryText,
        int textCandidateLimit,
        BsonArray? searchFilter,
        double vectorWeight,
        double textWeight,
        bool includeScoreDetails,
        int limit)
    {
        BsonDocument vectorSearchStage = BuildVectorSearchStage(
            vectorIndexName,
            vectorFieldName,
            queryVector,
            vectorCandidateLimit,
            exact: false,
            vectorNumCandidates,
            vectorFilter);
        BsonDocument textSearchStage = BuildFullTextSearchStage(searchIndexName, textFieldNames, queryText, searchFilter);

        Dictionary<string, PipelineDefinition<BsonDocument, BsonDocument>> pipelines = new()
        {
            ["vector"] = new BsonDocument[] { vectorSearchStage },
            ["text"] = new BsonDocument[] { textSearchStage, new BsonDocument("$limit", textCandidateLimit) },
        };
        Dictionary<string, double> weights = new() { ["vector"] = vectorWeight, ["text"] = textWeight };
        var rankFusionOptions = new RankFusionOptions<BsonDocument> { ScoreDetails = includeScoreDetails };
        PipelineStageDefinition<BsonDocument, BsonDocument> rankFusionStage =
            PipelineStageDefinitionBuilder.RankFusion<BsonDocument, BsonDocument>(pipelines, weights, rankFusionOptions);

        var stages = new List<BsonDocument>
        {
            rankFusionStage.Render(RenderArgs).Document,
            new BsonDocument("$limit", limit),
            ScoreAliasStage("score"),
        };
        if (includeScoreDetails)
        {
            stages.Add(new BsonDocument(
                "$set",
                new BsonDocument(FieldPath.ReservedScoreDetailsAlias, new BsonDocument("$meta", "scoreDetails"))));
        }

        return [.. stages];
    }

    private static BsonDocument BuildVectorSearchStage(
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

        return vectorSearchStage.Render(RenderArgs).Document;
    }

    private static BsonDocument BuildFullTextSearchStage(
        string indexName,
        IReadOnlyList<string> textFieldNames,
        string queryText,
        BsonArray? filter)
    {
        var textClause = new BsonDocument(
            "text",
            new BsonDocument { { "query", queryText }, { "path", TextPath(textFieldNames) } });
        var compound = new BsonDocument("must", new BsonArray { textClause });
        if (filter is not null)
        {
            compound.Add("filter", filter);
        }

        var searchOptions = new SearchOptions<BsonDocument> { IndexName = indexName };
        PipelineStageDefinition<BsonDocument, BsonDocument> searchStage =
            PipelineStageDefinitionBuilder.Search<BsonDocument>(
                new BsonDocumentSearchDefinition<BsonDocument>(new BsonDocument("compound", compound)),
                searchOptions);

        return searchStage.Render(RenderArgs).Document;
    }

    private static BsonDocument ScoreAliasStage(string metaKeyword) =>
        new("$set", new BsonDocument(FieldPath.ReservedScoreAlias, new BsonDocument("$meta", metaKeyword)));

    private static BsonValue TextPath(IReadOnlyList<string> textFieldNames) =>
        textFieldNames.Count == 1 ? textFieldNames[0] : new BsonArray(textFieldNames);
}
