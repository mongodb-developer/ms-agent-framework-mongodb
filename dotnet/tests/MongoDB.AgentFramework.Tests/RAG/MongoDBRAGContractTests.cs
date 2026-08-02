using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.RAG;

/// <summary>
/// Asserts the language-neutral contract that a configured <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>
/// is completely translated and placed inside the active <c>$vectorSearch</c> stage for both ANN and ENN modes.
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
}
