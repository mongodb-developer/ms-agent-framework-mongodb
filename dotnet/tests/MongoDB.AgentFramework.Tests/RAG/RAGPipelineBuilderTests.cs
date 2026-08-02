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
            filter: filter,
            projection: new BsonDocument("text", 1));

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
            filter: null,
            projection: new BsonDocument("text", 1));

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
            filter: null,
            projection: new BsonDocument("text", 1));

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
            filter: null,
            projection: new BsonDocument("text", 1)));
    }

    [Fact]
    public void Pipeline_appends_score_and_projection_stages_after_vectorSearch_in_order()
    {
        var projection = new BsonDocument { { "text", 1 }, { "_ragScore", 1 } };

        BsonDocument[] stages = RAGPipelineBuilder.BuildVectorSearchPipeline(
            indexName: "vector_index",
            vectorFieldName: "embedding",
            queryVector: QueryVector,
            limit: 5,
            exact: false,
            numCandidates: 50,
            filter: null,
            projection: projection);

        Assert.Equal(3, stages.Length);
        Assert.True(stages[0].Contains("$vectorSearch"));
        Assert.Equal(
            BsonDocument.Parse("""{"$set":{"_ragScore":{"$meta":"vectorSearchScore"}}}"""),
            stages[1]);
        Assert.Equal(new BsonDocument("$project", projection), stages[2]);
    }

    [Fact]
    public void BuildProjection_includes_configured_fields_and_the_ragScore_alias()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            IdFieldName = "_id",
            ChunkTextFieldName = "text",
            SourceNameFieldName = "source.name",
            SourceUrlFieldName = "source.url",
            MetadataFieldNames = ["category", "source.name"],
        };

        BsonDocument projection = RAGPipelineBuilder.BuildProjection(options);

        Assert.Equal(1, projection["_id"].AsInt32);
        Assert.Equal(1, projection["text"].AsInt32);
        Assert.Equal(1, projection["source.name"].AsInt32);
        Assert.Equal(1, projection["source.url"].AsInt32);
        Assert.Equal(1, projection["category"].AsInt32);
        Assert.Equal(1, projection["_ragScore"].AsInt32);
        // A metadata field duplicating the source-name field must not produce two entries.
        Assert.Equal(6, projection.ElementCount);
    }

    [Fact]
    public void BuildProjection_omits_unconfigured_optional_fields()
    {
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            SourceNameFieldName = null,
            SourceUrlFieldName = null,
            MetadataFieldNames = null,
        };

        BsonDocument projection = RAGPipelineBuilder.BuildProjection(options);

        Assert.False(projection.Contains("source.name"));
        Assert.False(projection.Contains("source.url"));
    }
}
