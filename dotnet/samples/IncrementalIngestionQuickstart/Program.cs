using Microsoft.Extensions.AI;
using MongoDB.AgentFramework;
using MongoDB.AgentFramework.Samples.Ingestion;
using MongoDB.Bson;
using MongoDB.Driver;

// This sample demonstrates the sample-only incremental ingestion pipeline (docs/spec/samples.md's
// "IncrementalIngestion" sample and docs/spec/features/ingestion.md): a bounded local directory reader, a
// deterministic chunker, and a bulk-upsert pipeline that skips unchanged chunks, embeds/upserts only new or
// changed chunks, and safely deletes chunks the current content no longer produces -- all scoped to one
// tenant+source. None of this is part of MongoDB.AgentFramework's public runtime API; it is sample-local code
// reusable by any application via a project/source reference.
string uri = Environment.GetEnvironmentVariable("MONGODB_URI")
    ?? throw new InvalidOperationException("Set MONGODB_URI.");
string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")
    ?? throw new InvalidOperationException("Set MONGODB_DATABASE.");
string collectionName = Environment.GetEnvironmentVariable("MONGODB_INGESTION_COLLECTION")
    ?? "agent_framework_ingestion_chunks";
const string TenantId = "quickstart";
const string SourceId = "incremental-quickstart-doc";

using var client = new MongoClient(uri);
IMongoCollection<BsonDocument> collection = client.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);
var store = new MongoChunkStore(collection);
var embedder = new BatchEmbedder(new SampleEmbeddingGenerator(), dimensions: 3);
var pipeline = new IncrementalIngestionPipeline(store, embedder, new ChunkingOptions { WindowSize = 200, OverlapSize = 40 });

Console.WriteLine("Run 1: first ingestion of the source document.");
var original = new SourceDocument(
    TenantId,
    SourceId,
    "Widgets ship in blue by default. Gadgets ship in red by default. Both items ship within two business days. " +
        "Customers may request expedited shipping for an additional fee.",
    Title: "Shipping FAQ");
IngestionResult first = await pipeline.IngestAsync(original);
Console.WriteLine($"  upserted={first.ChunksUpserted} unchanged={first.ChunksUnchanged} deleted={first.ChunksDeleted}");

Console.WriteLine("Run 2: re-ingesting identical content -- everything should be unchanged.");
IngestionResult rerun = await pipeline.IngestAsync(original);
Console.WriteLine($"  upserted={rerun.ChunksUpserted} unchanged={rerun.ChunksUnchanged} deleted={rerun.ChunksDeleted}");

Console.WriteLine("Run 3: ingesting shorter, changed content -- stale chunks are deleted.");
var updated = original with
{
    Content = "Widgets ship in blue by default. Expedited shipping now ships within one business day.",
};
IngestionResult changed = await pipeline.IngestAsync(updated);
Console.WriteLine($"  upserted={changed.ChunksUpserted} unchanged={changed.ChunksUnchanged} deleted={changed.ChunksDeleted}");

Console.WriteLine();
Console.WriteLine("Cleaning up this quickstart's own chunks (bounded, tenant+source-scoped delete).");
IReadOnlyDictionary<string, string> remainingHashes = await store.GetExistingHashesAsync(TenantId, SourceId);
int deletedCount = await store.DeleteAsync(TenantId, SourceId, [.. remainingHashes.Keys]);
Console.WriteLine($"  deleted={deletedCount}");

sealed class SampleEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(static value => new Embedding<float>(
                value.Contains("widget", StringComparison.OrdinalIgnoreCase)
                    ? new float[] { 1, 0, 0 }
                    : new float[] { 0, 1, 0 }))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
