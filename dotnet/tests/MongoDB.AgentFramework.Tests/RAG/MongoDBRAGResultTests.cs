using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.RAG;

public sealed class MongoDBRAGResultTests
{
    [Fact]
    public void RejectsEmptyId()
    {
        Assert.Throws<MongoDBConfigurationException>(
            () => new MongoDBRAGResult(string.Empty, "chunk text", 0.9));
    }

    [Fact]
    public void RejectsNullText()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MongoDBRAGResult("doc-1", null!, 0.9));
    }

    [Fact]
    public void DefaultsToEmptyMetadataAndRawDocument()
    {
        var result = new MongoDBRAGResult("doc-1", "chunk text", 0.9);

        Assert.Empty(result.Metadata);
        Assert.Equal(new BsonDocument(), result.RawDocument);
    }

    [Fact]
    public void PreservesRawDocumentContent()
    {
        var raw = new BsonDocument { { "_id", "doc-1" }, { "text", "chunk text" } };

        var result = new MongoDBRAGResult("doc-1", "chunk text", 0.9, rawDocument: raw);

        Assert.Equal(raw, result.RawDocument);
    }

    [Fact]
    public void RawDocumentIsImmutableAgainstLaterMutationOfTheSourceDocument()
    {
        var raw = new BsonDocument { { "_id", "doc-1" } };
        var result = new MongoDBRAGResult("doc-1", "chunk text", 0.9, rawDocument: raw);

        raw.Add("mutated_after_construction", true);

        Assert.False(result.RawDocument.Contains("mutated_after_construction"));
    }

    [Fact]
    public void MetadataIsImmutableAgainstLaterMutationOfTheSourceDictionary()
    {
        var metadata = new Dictionary<string, BsonValue> { { "category", "news" } };
        var result = new MongoDBRAGResult("doc-1", "chunk text", 0.9, metadata: metadata);

        metadata["mutated_after_construction"] = true;

        Assert.False(result.Metadata.ContainsKey("mutated_after_construction"));
        Assert.Throws<NotSupportedException>(() =>
        {
            ((IDictionary<string, BsonValue>)result.Metadata)["x"] = true;
        });
    }

    [Fact]
    public void PreservesSourceAttribution()
    {
        var result = new MongoDBRAGResult(
            "doc-1",
            "chunk text",
            0.75,
            sourceName: "Knowledge Base Article",
            sourceUrl: "https://example.test/kb/1");

        Assert.Equal("Knowledge Base Article", result.SourceName);
        Assert.Equal("https://example.test/kb/1", result.SourceUrl);
        Assert.Equal(0.75, result.Score);
    }
}
