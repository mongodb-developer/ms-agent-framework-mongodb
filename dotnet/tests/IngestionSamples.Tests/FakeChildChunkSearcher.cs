using MongoDB.AgentFramework.Samples.Ingestion;
using MongoDB.Bson;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

/// <summary>An in-memory <see cref="IChildChunkSearcher"/> substitute used only by offline retriever tests.</summary>
internal sealed class FakeChildChunkSearcher : IChildChunkSearcher
{
    private readonly IReadOnlyList<MongoDBRAGResult> _results;

    public FakeChildChunkSearcher(IReadOnlyList<MongoDBRAGResult> results)
    {
        _results = results;
    }

    public string? LastQuery { get; private set; }

    public Task<IReadOnlyList<MongoDBRAGResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastQuery = query;
        return Task.FromResult(_results);
    }

    public static MongoDBRAGResult ChildResult(string id, double score, string parentId, string? sourceName = null) =>
        new(
            id,
            text: $"child text for {id}",
            score: score,
            sourceName: sourceName,
            metadata: new Dictionary<string, BsonValue> { ["parent_id"] = parentId });

    public static MongoDBRAGResult ChildResultWithoutParent(string id, double score) =>
        new(id, text: $"child text for {id}", score: score);
}
