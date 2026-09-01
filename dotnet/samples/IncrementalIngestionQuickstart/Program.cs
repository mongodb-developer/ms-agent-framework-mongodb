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
bool keepData = args.Contains("--keep-data", StringComparer.Ordinal);
string[] unknownArguments = args.Where(argument => argument != "--keep-data").ToArray();
if (unknownArguments.Length > 0)
{
    throw new ArgumentException($"Unknown argument(s): {string.Join(", ", unknownArguments)}");
}
const string TenantId = "quickstart";
const string SourceId = "incremental-quickstart-doc";
const string SecondSourceId = "incremental-quickstart-doc-2";

using var client = new MongoClient(uri);
IMongoCollection<BsonDocument> collection = client.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);
var store = new MongoChunkStore(collection);
var embedder = new BatchEmbedder(new SampleEmbeddingGenerator(), dimensions: 3);
var pipeline = new IncrementalIngestionPipeline(store, embedder, new ChunkingOptions { WindowSize = 200, OverlapSize = 40 });

// The Vector Search index name is never user-supplied: it is always a freshly generated, sample-prefixed, unique
// name for this run, so cleanup can only ever drop an index this run itself created -- never an arbitrary
// pre-existing or user-configured index.
string vectorIndexName = $"agent_framework_sample_incr_{Guid.NewGuid():N}";
var vectorDefinition = new MongoDBVectorSearchIndexDefinition(
    vectorIndexName,
    "embedding",
    vectorDimensions: 3,
    similarity: "cosine",
    filterFieldPaths: [ChunkRecord.TenantIdFieldName]);
await using var indexManager = new MongoDBRAGIndexManager(collection, vectorDefinition);

// GeneratedIndexProvisioner records ownership *before* attempting to create the index (not only after success),
// so a failure partway through provisioning (e.g. the index is created but the bounded wait for READY times out)
// still leaves ownership correctly recorded, and the SampleCleanupOrchestration.RunAsync call below still
// attempts to drop it rather than leaking it.
var provisioner = new GeneratedIndexProvisioner(
    existsAsync: async ct => await indexManager.GetVectorSearchIndexAsync(ct) is not null,
    ensureAsync: async ct =>
    {
        Console.WriteLine("Provisioning this run's own Vector Search index (this can take a while on a fresh cluster)...");
        await indexManager.EnsureVectorSearchIndexAsync(waitUntilReady: true, timeout: TimeSpan.FromMinutes(3), cancellationToken: ct);
    },
    validateAsync: async ct =>
    {
        Console.WriteLine("This run's generated index name already exists; validating rather than re-creating it.");
        await indexManager.ValidateVectorSearchIndexAsync(cancellationToken: ct);
    });

// The outer try/finally boundary starts *before* provisioning (via SampleCleanupOrchestration.RunAsync wrapping
// the body below), not only around the ingestion runs: if provisioning itself throws after having created the
// index (see GeneratedIndexProvisioner above), cleanup still runs and still attempts to drop it. Cleanup steps
// are each attempted independently -- an index-drop failure never prevents the document-delete attempt, and vice
// versa -- and a primary body failure is never silently hidden by a later cleanup failure.
await SampleCleanupOrchestration.RunAsync(
    body: async () =>
    {
        Console.WriteLine("Run 1: first ingestion of the source document.");
        var original = new SourceDocument(
            TenantId,
            SourceId,
            "Widgets ship in blue by default. Gadgets ship in red by default. Both items ship within two business days. " +
                "Customers may request expedited shipping for an additional fee.",
            Title: "Shipping FAQ");
        IngestionResult first = await pipeline.IngestAsync(original);
        Console.WriteLine($"  upserted={first.ChunksUpserted} unchanged={first.ChunksUnchanged} deleted={first.ChunksDeleted}");

        await provisioner.ProvisionAsync();

        Console.WriteLine("Run 2: re-ingesting identical content -- everything should be unchanged.");
        IngestionResult rerun = await pipeline.IngestAsync(original);
        Console.WriteLine($"  upserted={rerun.ChunksUpserted} unchanged={rerun.ChunksUnchanged} deleted={rerun.ChunksDeleted}");

        if (keepData)
        {
            Console.WriteLine("Deletion demonstrations skipped by --keep-data.");
        }
        else
        {
            Console.WriteLine("Run 3: ingesting shorter, changed content -- stale chunks are deleted.");
            var updated = original with
            {
                Content = "Widgets ship in blue by default. Expedited shipping now ships within one business day.",
            };
            IngestionResult changed = await pipeline.IngestAsync(updated);
            Console.WriteLine($"  upserted={changed.ChunksUpserted} unchanged={changed.ChunksUnchanged} deleted={changed.ChunksDeleted}");

            Console.WriteLine();
            Console.WriteLine("Run 4: ingesting a second source, then reconciling it away once it disappears from the manifest.");
            var secondSource = new SourceDocument(TenantId, SecondSourceId, "A second, unrelated source document.", Title: "Second doc");
            IngestionResult secondResult = await pipeline.IngestAsync(secondSource);
            Console.WriteLine($"  upserted={secondResult.ChunksUpserted} unchanged={secondResult.ChunksUnchanged} deleted={secondResult.ChunksDeleted}");

            // SourceManifestReconciler is the complement to IncrementalIngestionPipeline's own per-source stale-chunk
            // cleanup: it tombstones sources that have disappeared from the corpus entirely (are no longer produced at
            // all), which the per-source pipeline above would never revisit on its own. Here, the second source is
            // deliberately omitted from the "currently known" manifest to simulate it having disappeared.
            var reconciler = new SourceManifestReconciler(store);
            SourceReconciliationResult reconciliation = await reconciler.ReconcileAsync(TenantId, [SourceId]);
            Console.WriteLine($"  disappeared sources={string.Join(", ", reconciliation.DisappearedSourceIds)} " +
                $"recordsDeleted={reconciliation.RecordsDeleted}");
        }
    },
    async () =>
    {
        if (keepData)
        {
            Console.WriteLine();
            Console.WriteLine("Authorized document cleanup skipped by --keep-data.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Cleaning up this quickstart's own chunks (bounded, tenant+source-scoped delete).");
        int deletedCount = await store.DeleteSourceAsync(TenantId, SourceId);
        deletedCount += await store.DeleteSourceAsync(TenantId, SecondSourceId);
        Console.WriteLine($"  deleted={deletedCount}");
    },
    async () =>
    {
        // The index is dropped only if this run created it -- never an arbitrary configured or pre-existing
        // index. DropVectorSearchIndexAsync is itself a safe no-op if the index turns out to be absent (for
        // example if creation never actually got far enough to succeed).
        if (provisioner.CreatedByThisRun)
        {
            Console.WriteLine("Cleaning up this run's own Vector Search index.");
            await indexManager.DropVectorSearchIndexAsync();
        }
    });

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
