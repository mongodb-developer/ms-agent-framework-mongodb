using MongoDB.AgentFramework.Internal;
using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.RAG;

public sealed class RAGPipelineBuilderTests
{
    private static readonly float[] QueryVector = [0.1f, 0.2f, 0.3f];

    [Fact]
    public void Ann_stage_places_numCandidates_and_filter_inside_vectorSearch()
    {
        BsonDocument filter = BsonDocument.Parse("""{"tenant_id":"tenant-a"}""");

        BsonDocument[] stages = RAGPipelineBuilder.BuildVectorSearchPipeline(
            indexName: "vector_index",
            vectorFieldName: "embedding",
            queryVector: QueryVector,
            limit: 5,
            exact: false,
            numCandidates: 150,
            filter: filter);

        BsonDocument vectorSearch = stages[0]["$vectorSearch"].AsBsonDocument;
        Assert.Equal("vector_index", vectorSearch["index"].AsString);
        Assert.Equal("embedding", vectorSearch["path"].AsString);
        Assert.Equal(5, vectorSearch["limit"].AsInt32);
        Assert.Equal(150, vectorSearch["numCandidates"].AsInt32);
        Assert.Equal(filter, vectorSearch["filter"].AsBsonDocument);
        Assert.False(vectorSearch.Contains("exact"));
        Assert.Equal(
            new BsonArray(QueryVector.Select(value => (BsonValue)value)),
            vectorSearch["queryVector"].AsBsonArray);
    }

    [Fact]
    public void Enn_stage_sets_exact_true_and_omits_numCandidates()
    {
        BsonDocument[] stages = RAGPipelineBuilder.BuildVectorSearchPipeline(
            indexName: "vector_index",
            vectorFieldName: "embedding",
            queryVector: QueryVector,
            limit: 5,
            exact: true,
            numCandidates: null,
            filter: null);

        BsonDocument vectorSearch = stages[0]["$vectorSearch"].AsBsonDocument;
        Assert.True(vectorSearch["exact"].AsBoolean);
        Assert.False(vectorSearch.Contains("numCandidates"));
        Assert.False(vectorSearch.Contains("filter"));
    }

    [Fact]
    public void Filter_is_omitted_from_the_stage_when_there_is_no_effective_filter()
    {
        BsonDocument[] stages = RAGPipelineBuilder.BuildVectorSearchPipeline(
            indexName: "vector_index",
            vectorFieldName: "embedding",
            queryVector: QueryVector,
            limit: 5,
            exact: false,
            numCandidates: 50,
            filter: null);

        BsonDocument vectorSearch = stages[0]["$vectorSearch"].AsBsonDocument;
        Assert.False(vectorSearch.Contains("filter"));
    }

    [Fact]
    public void Exact_and_numCandidates_together_are_rejected_before_any_stage_is_built()
    {
        Assert.Throws<MongoDBConfigurationException>(() => RAGPipelineBuilder.BuildVectorSearchPipeline(
            indexName: "vector_index",
            vectorFieldName: "embedding",
            queryVector: QueryVector,
            limit: 5,
            exact: true,
            numCandidates: 10,
            filter: null));
    }

    [Fact]
    public void Pipeline_appends_only_the_score_stage_after_vectorSearch_and_does_not_narrow_the_document()
    {
        BsonDocument[] stages = RAGPipelineBuilder.BuildVectorSearchPipeline(
            indexName: "vector_index",
            vectorFieldName: "embedding",
            queryVector: QueryVector,
            limit: 5,
            exact: false,
            numCandidates: 50,
            filter: null);

        // Exactly two stages: $vectorSearch and the $set score stage. There must be no trailing $project stage,
        // since narrowing the result there would discard fields of the original document that were not explicitly
        // configured, breaking the guarantee that MongoDBRAGResult.RawDocument preserves the complete document.
        Assert.Equal(2, stages.Length);
        Assert.True(stages[0].Contains("$vectorSearch"));
        Assert.Equal(
            BsonDocument.Parse("""{"$set":{"_ragScore":{"$meta":"vectorSearchScore"}}}"""),
            stages[1]);
        Assert.DoesNotContain(stages, stage => stage.Contains("$project"));
    }

    [Fact]
    public void The_score_stage_uses_the_shared_reserved_alias_constant()
    {
        BsonDocument[] stages = RAGPipelineBuilder.BuildVectorSearchPipeline(
            indexName: "vector_index",
            vectorFieldName: "embedding",
            queryVector: QueryVector,
            limit: 5,
            exact: false,
            numCandidates: 50,
            filter: null);

        BsonDocument setStage = stages[1]["$set"].AsBsonDocument;
        Assert.True(setStage.Contains(FieldPath.ReservedScoreAlias));
    }
}
