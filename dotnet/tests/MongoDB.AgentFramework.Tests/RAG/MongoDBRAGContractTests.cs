using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.RAG;

/// <summary>
/// Asserts the language-neutral contract that a configured <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>
/// is completely translated and placed inside the active <c>$vectorSearch</c> stage for both ANN and ENN modes, and
/// inside the active <c>$search</c> <c>compound.filter</c> array for <see cref="MongoDBSearchMode.FullText"/>.
/// There is no Python RAG implementation yet to share a cross-language JSON fixture with (unlike Memory's
/// <c>scope-filters.json</c>); this test instead exercises the full filter AST end-to-end through the real
/// retrieval pipeline, complementing the unit-level <c>RAGFilterTranslator</c> tests from the contracts slice.
/// </summary>
public sealed class MongoDBRAGContractTests
{
    [Theory]
    [InlineData(MongoDBSearchMode.VectorAnn)]
    [InlineData(MongoDBSearchMode.VectorEnn)]
    public async Task MandatoryFilterIsCompletelyTranslatedInsideTheVectorSearchStage(MongoDBSearchMode mode)
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
            MongoDBRAGFilter.Or(
                MongoDBRAGFilter.In("category", ["docs", "faq"]),
                MongoDBRAGFilter.Range("published_at", minimum: 0, maximum: null)));
        var state = new RAGCollectionState();
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = mode,
            MandatoryFilter = filter,
        };
        MongoDBRAGProvider provider = new(
            RAGCollectionProxy.Create(state),
            new RecordingEmbeddingGenerator(),
            3,
            options);

        await provider.SearchAsync("contract query");

        BsonDocument actual = state.AggregateStages[0]["$vectorSearch"]["filter"].AsBsonDocument;
        BsonDocument expected = BsonDocument.Parse("""
            {
                "$and": [
                    { "tenant_id": { "$eq": "tenant-a" } },
                    {
                        "$or": [
                            { "category": { "$in": ["docs", "faq"] } },
                            { "published_at": { "$gte": 0.0 } }
                        ]
                    }
                ]
            }
            """);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task MandatoryFilterIsCompletelyTranslatedInsideTheSearchCompoundFilter()
    {
        MongoDBRAGFilter filter = MongoDBRAGFilter.And(
            MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
            MongoDBRAGFilter.Or(
                MongoDBRAGFilter.In("category", ["docs", "faq"]),
                MongoDBRAGFilter.Range("published_at", minimum: 0, maximum: null)));
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "chunk-1" }, { "text", "chunk" }, { "_ragScore", 1.0 } }],
        };
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            MandatoryFilter = filter,
        };
        MongoDBRAGProvider provider = new(RAGCollectionProxy.Create(state), options);

        await provider.SearchAsync("contract query");

        BsonArray actual = state.AggregateStages[0]["$search"]["compound"]["filter"].AsBsonArray;
        BsonArray expected = BsonDocument.Parse("""
            {
                "filter": [
                    { "equals": { "path": "tenant_id", "value": "tenant-a" } },
                    {
                        "compound": {
                            "should": [
                                { "in": { "path": "category", "value": ["docs", "faq"] } },
                                { "range": { "path": "published_at", "gte": 0.0 } }
                            ],
                            "minimumShouldMatch": 1
                        }
                    }
                ]
            }
            """)["filter"].AsBsonArray;
        Assert.Equal(expected, actual);
    }
}
