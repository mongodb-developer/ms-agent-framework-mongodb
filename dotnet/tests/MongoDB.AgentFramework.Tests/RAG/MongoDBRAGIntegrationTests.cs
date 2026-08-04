using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.RAG;

/// <summary>
/// Exercises live ANN, ENN, and FullText retrieval against a pre-provisioned MongoDB Atlas deployment. Index
/// provisioning is out of scope for this slice (see <c>docs/development/rag/dotnet-rag-vector-search.md</c> and
/// <c>docs/development/rag/dotnet-rag-full-text-search.md</c>), so unlike the Memory integration test this fixture
/// cannot create its own Vector Search or Search index per run. Instead it targets a fixed, operator-provisioned
/// collection and indexes (documented via <see cref="MongoIntegrationFactAttribute"/>) and only ever writes/deletes
/// documents whose IDs carry a unique, test-owned prefix, so concurrent runs and the shared index definitions are
/// unaffected.
/// </summary>
public sealed class MongoDBRAGIntegrationTests
{
    [MongoIntegrationFact]
    [Trait("Category", "integration-rag")]
    public async Task VectorAnnAndEnnSearchIsolateTenantsOnAPreProvisionedIndex()
    {
        string? uri = Environment.GetEnvironmentVariable("MONGODB_URI");
        string? databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE");
        string collectionName = Environment.GetEnvironmentVariable("MONGODB_RAG_COLLECTION") ??
            "af_rag_dotnet_integration";
        string vectorIndexName = Environment.GetEnvironmentVariable("MONGODB_RAG_VECTOR_INDEX") ??
            "agent_framework_rag_vector";
        Assert.False(string.IsNullOrWhiteSpace(uri));
        Assert.False(string.IsNullOrWhiteSpace(databaseName));

        using var client = new MongoClient(uri!);
        IMongoCollection<BsonDocument> collection = client
            .GetDatabase(databaseName!)
            .GetCollection<BsonDocument>(collectionName);
        string prefix = $"af_rag_dotnet_test_{Guid.NewGuid():N}_";
        string tenantAId = $"{prefix}a";
        string tenantBId = $"{prefix}b";
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorIndexName = vectorIndexName,
            TopK = 10,
            MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
        };
        MongoDBRAGProvider provider = new(
            client,
            databaseName!,
            collectionName,
            new RecordingEmbeddingGenerator(),
            3,
            options);

        // No MandatoryFilter: used only to independently confirm both tenant documents are searchable (via each
        // search mode) before that mode's tenant-A-scoped provider's exclusion of tenant B is asserted below.
        // Without this, the exclusion assertions could pass vacuously merely because tenant B's document was never
        // indexed/searchable in the first place (Atlas Search/Vector Search index newly written documents
        // asynchronously), rather than because the mandatory filter actually excluded it.
        var annReadinessOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorIndexName = vectorIndexName,
            TopK = 10,
        };
        await using MongoDBRAGProvider annReadinessProvider = new(
            client,
            databaseName!,
            collectionName,
            new RecordingEmbeddingGenerator(),
            3,
            annReadinessOptions);

        var ennOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorEnn,
            VectorIndexName = vectorIndexName,
            TopK = 10,
            MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
        };
        await using MongoDBRAGProvider ennProvider = new(
            client,
            databaseName!,
            collectionName,
            new RecordingEmbeddingGenerator(),
            3,
            ennOptions);

        var ennReadinessOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorEnn,
            VectorIndexName = vectorIndexName,
            TopK = 10,
        };
        await using MongoDBRAGProvider ennReadinessProvider = new(
            client,
            databaseName!,
            collectionName,
            new RecordingEmbeddingGenerator(),
            3,
            ennReadinessOptions);
        try
        {
            await collection.InsertManyAsync(
            [
                new BsonDocument
                {
                    { "_id", tenantAId },
                    { "text", "Widgets ship in blue for tenant A." },
                    { "embedding", new BsonArray([1.0, 0.0, 0.0]) },
                    { "tenant_id", "tenant-a" },
                },
                new BsonDocument
                {
                    { "_id", tenantBId },
                    { "text", "Cross-tenant content must not be returned." },
                    { "embedding", new BsonArray([1.0, 0.0, 0.0]) },
                    { "tenant_id", "tenant-b" },
                },
            ]);

            // ANN readiness: poll independently of ENN, since $vectorSearch's approximate index and an ENN exact
            // scan can become consistent at different times after the same insert.
            await PollUntilSearchableAsync(
                annReadinessProvider,
                "blue widgets",
                results => results.Any(r => r.Id == tenantAId) && results.Any(r => r.Id == tenantBId),
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));

            IReadOnlyList<MongoDBRAGResult> annResults = await PollUntilSearchableAsync(
                provider,
                "blue widgets",
                tenantAId,
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));
            Assert.Contains(annResults, result => result.Id == tenantAId);
            Assert.DoesNotContain(annResults, result => result.Id == tenantBId);

            // RawDocument must preserve the complete original document against a real MongoDB deployment, not just
            // the fields the mapping configuration narrows to, and the internal reserved score alias must never
            // leak into it.
            MongoDBRAGResult tenantAAnnResult = Assert.Single(annResults, result => result.Id == tenantAId);
            Assert.Equal("tenant-a", tenantAAnnResult.RawDocument["tenant_id"].AsString);
            Assert.False(tenantAAnnResult.RawDocument.Contains("_ragScore"));

            // ENN readiness: polled independently of ANN above (separate provider, separate bounded deadline), per
            // the same non-vacuous-isolation reasoning.
            await PollUntilSearchableAsync(
                ennReadinessProvider,
                "blue widgets",
                results => results.Any(r => r.Id == tenantAId) && results.Any(r => r.Id == tenantBId),
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));

            IReadOnlyList<MongoDBRAGResult> ennResults = await PollUntilSearchableAsync(
                ennProvider,
                "blue widgets",
                tenantAId,
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));
            Assert.Contains(ennResults, result => result.Id == tenantAId);
            Assert.DoesNotContain(ennResults, result => result.Id == tenantBId);
        }
        finally
        {
            Assert.StartsWith("af_rag_dotnet_test_", prefix);
            await collection.DeleteManyAsync(
                Builders<BsonDocument>.Filter.In("_id", new[] { tenantAId, tenantBId }));
            await provider.DisposeAsync();
        }
    }

    internal sealed class MongoIntegrationFactAttribute : FactAttribute
    {
        public MongoIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_URI")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_DATABASE")))
            {
                Skip = "MONGODB_URI and MONGODB_DATABASE are required for integration-rag. " +
                    "This fixture additionally requires an operator-provisioned collection " +
                    "(MONGODB_RAG_COLLECTION, default 'af_rag_dotnet_integration') with a ready " +
                    "3-dimension cosine Vector Search index (MONGODB_RAG_VECTOR_INDEX, default " +
                    "'agent_framework_rag_vector') over its 'embedding' field, since index " +
                    "provisioning is out of scope for this slice.";
            }
        }
    }

    /// <summary>
    /// Bounded polling that repeatedly invokes <see cref="MongoDBRAGProvider.SearchAsync(string, CancellationToken)"/>
    /// until <paramref name="isReady"/> accepts its results or <paramref name="timeout"/> elapses. Atlas Search
    /// indexes newly written documents asynchronously, so a single immediate query after <c>InsertManyAsync</c>
    /// can race the index and flake; this exists only to make the test/sample deterministic and is not part of the
    /// production <see cref="MongoDBRAGProvider"/> contract, which never polls on a caller's behalf. Cancellation
    /// always propagates as a clear <see cref="TimeoutException"/> rather than a bare
    /// <see cref="OperationCanceledException"/>, so a failure unambiguously reads as "index lag exceeded the
    /// bounded wait", not a product defect.
    /// </summary>
    private static async Task<IReadOnlyList<MongoDBRAGResult>> PollUntilSearchableAsync(
        MongoDBRAGProvider provider,
        string query,
        Func<IReadOnlyList<MongoDBRAGResult>, bool> isReady,
        TimeSpan timeout,
        TimeSpan pollInterval)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                IReadOnlyList<MongoDBRAGResult> results = await provider.SearchAsync(query, cts.Token);
                if (isReady(results))
                {
                    return results;
                }

                await Task.Delay(pollInterval, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for the expected document(s) to become searchable for " +
                $"query '{query}'. This indicates Atlas Search indexing lag exceeded the bounded poll window, " +
                "not a MongoDBRAGProvider defect.");
        }
    }

    /// <summary>Convenience overload of <see cref="PollUntilSearchableAsync"/> for a single expected document ID.</summary>
    private static Task<IReadOnlyList<MongoDBRAGResult>> PollUntilSearchableAsync(
        MongoDBRAGProvider provider,
        string query,
        string expectedId,
        TimeSpan timeout,
        TimeSpan pollInterval) =>
        PollUntilSearchableAsync(
            provider,
            query,
            results => results.Any(result => result.Id == expectedId),
            timeout,
            pollInterval);

    [MongoIntegrationFact]
    [Trait("Category", "integration-rag-search")]
    public async Task FullTextSearchIsolatesTenantsOnAPreProvisionedIndex()
    {
        string? uri = Environment.GetEnvironmentVariable("MONGODB_URI");
        string? databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE");
        string collectionName = Environment.GetEnvironmentVariable("MONGODB_RAG_COLLECTION") ??
            "af_rag_dotnet_integration";
        string searchIndexName = Environment.GetEnvironmentVariable("MONGODB_RAG_SEARCH_INDEX") ??
            "agent_framework_rag_search";
        Assert.False(string.IsNullOrWhiteSpace(uri));
        Assert.False(string.IsNullOrWhiteSpace(databaseName));

        using var client = new MongoClient(uri!);
        IMongoCollection<BsonDocument> collection = client
            .GetDatabase(databaseName!)
            .GetCollection<BsonDocument>(collectionName);
        string prefix = $"af_rag_dotnet_test_{Guid.NewGuid():N}_";
        string tenantAId = $"{prefix}a";
        string tenantBId = $"{prefix}b";
        var options = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            SearchIndexName = searchIndexName,
            SearchTextFieldNames = ["text"],
            TopK = 10,
            MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
        };
        await using MongoDBRAGProvider provider = new(client, databaseName!, collectionName, options);

        // No MandatoryFilter: used only to independently confirm both tenant documents are searchable at all
        // before the tenant-A-scoped provider's exclusion of tenant B is asserted below. Without this, that
        // exclusion assertion could pass vacuously merely because tenant B was never indexed/searchable in the
        // first place, rather than because the mandatory filter actually excluded it.
        var readinessOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            SearchIndexName = searchIndexName,
            SearchTextFieldNames = ["text"],
            TopK = 10,
        };
        await using MongoDBRAGProvider readinessProvider = new(client, databaseName!, collectionName, readinessOptions);
        try
        {
            await collection.InsertManyAsync(
            [
                new BsonDocument
                {
                    { "_id", tenantAId },
                    { "text", "Widgets ship in blue for tenant A." },
                    { "tenant_id", "tenant-a" },
                },
                new BsonDocument
                {
                    { "_id", tenantBId },
                    { "text", "Widgets also ship in blue for tenant B." },
                    { "tenant_id", "tenant-b" },
                },
            ]);

            await PollUntilSearchableAsync(
                readinessProvider,
                "blue widgets",
                results => results.Any(r => r.Id == tenantAId) && results.Any(r => r.Id == tenantBId),
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));

            IReadOnlyList<MongoDBRAGResult> results = await PollUntilSearchableAsync(
                provider,
                "blue widgets",
                tenantAId,
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));
            Assert.Contains(results, result => result.Id == tenantAId);
            Assert.DoesNotContain(results, result => result.Id == tenantBId);

            // RawDocument must preserve the complete original document against a real MongoDB deployment, and the
            // internal reserved score alias must never leak into it, matching the Vector Search contract.
            MongoDBRAGResult tenantAResult = Assert.Single(results, result => result.Id == tenantAId);
            Assert.Equal("tenant-a", tenantAResult.RawDocument["tenant_id"].AsString);
            Assert.False(tenantAResult.RawDocument.Contains("_ragScore"));
        }
        finally
        {
            Assert.StartsWith("af_rag_dotnet_test_", prefix);
            await collection.DeleteManyAsync(
                Builders<BsonDocument>.Filter.In("_id", new[] { tenantAId, tenantBId }));
        }
    }

    [MongoIntegrationFact]
    [Trait("Category", "integration-rag-hybrid")]
    public async Task HybridRrfSearchIsolatesTenantsOnPreProvisionedIndexes()
    {
        string? uri = Environment.GetEnvironmentVariable("MONGODB_URI");
        string? databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE");
        string collectionName = Environment.GetEnvironmentVariable("MONGODB_RAG_COLLECTION") ??
            "af_rag_dotnet_integration";
        string vectorIndexName = Environment.GetEnvironmentVariable("MONGODB_RAG_VECTOR_INDEX") ??
            "agent_framework_rag_vector";
        string searchIndexName = Environment.GetEnvironmentVariable("MONGODB_RAG_SEARCH_INDEX") ??
            "agent_framework_rag_search";
        Assert.False(string.IsNullOrWhiteSpace(uri));
        Assert.False(string.IsNullOrWhiteSpace(databaseName));

        using var client = new MongoClient(uri!);
        IMongoCollection<BsonDocument> collection = client
            .GetDatabase(databaseName!)
            .GetCollection<BsonDocument>(collectionName);
        string prefix = $"af_rag_dotnet_test_{Guid.NewGuid():N}_";
        string tenantAId = $"{prefix}a";
        string tenantBId = $"{prefix}b";
        string vectorMatchId = $"{prefix}vector-match";
        string textMatchId = $"{prefix}text-match";
        const string weightQuery = "distinctive weight sensitive query phrase";
        float[] weightQueryEmbedding = [1f, 0f, 0f];
        Func<string, float[]> weightEmbeddingFactory = query =>
            query == weightQuery ? weightQueryEmbedding : [0.1f, 0.1f, 0.1f];

        // No MandatoryFilter: used only to independently confirm both tenant documents are searchable through
        // *each* of Hybrid's two input branches (vector and text) before the tenant-A-scoped Hybrid provider's
        // exclusion of tenant B is asserted below. Without proving both branches are ready on their own, that
        // exclusion assertion could pass vacuously merely because tenant B was never indexed/searchable via one
        // or both branches in the first place, rather than because the mandatory filter actually excluded it.
        var vectorReadinessOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorIndexName = vectorIndexName,
            TopK = 10,
        };
        await using MongoDBRAGProvider vectorReadinessProvider = new(
            client, databaseName!, collectionName, new RecordingEmbeddingGenerator(), 3, vectorReadinessOptions);
        var textReadinessOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            SearchIndexName = searchIndexName,
            SearchTextFieldNames = ["text"],
            TopK = 10,
        };
        await using MongoDBRAGProvider textReadinessProvider = new(
            client, databaseName!, collectionName, textReadinessOptions);

        var hybridOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorIndexName = vectorIndexName,
            SearchIndexName = searchIndexName,
            SearchTextFieldNames = ["text"],
            TopK = 10,
            MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
        };
        await using MongoDBRAGProvider hybridProvider = new(
            client, databaseName!, collectionName, new RecordingEmbeddingGenerator(), 3, hybridOptions);

        // Explicit, strict (requireReady: true) capability validation ahead of any retrieval: proves both the
        // Vector Search and Search indexes exist and are READY/queryable up front, rather than only ever
        // discovering a misconfigured deployment implicitly as a side effect of the first SearchAsync call below
        // (SearchAsync also validates internally, but this asserts the seam directly per rag.md's capability
        // matrix).
        await hybridProvider.ValidateHybridSearchCapabilityAsync(requireReady: true, refresh: true);

        // A dedicated, non-tenant-filtered pair of Hybrid providers with opposite weight configurations, used
        // below to prove ordering is genuinely weight-sensitive rather than incidental: vectorMatchId's embedding
        // matches the query embedding exactly but its text shares no terms with the query, while textMatchId's
        // text contains the exact query phrase but its embedding is orthogonal to the query embedding. Neither
        // fixture ties with the other on both signals, so a correct implementation must rank each first only
        // under the weighting that favors its matching branch.
        var vectorHeavyOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorIndexName = vectorIndexName,
            SearchIndexName = searchIndexName,
            SearchTextFieldNames = ["text"],
            TopK = 10,
            VectorWeight = 10.0,
            TextWeight = 0.1,
        };
        await using MongoDBRAGProvider vectorHeavyProvider = new(
            client,
            databaseName!,
            collectionName,
            new RecordingEmbeddingGenerator { EmbeddingFactory = weightEmbeddingFactory },
            3,
            vectorHeavyOptions);
        var textHeavyOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorIndexName = vectorIndexName,
            SearchIndexName = searchIndexName,
            SearchTextFieldNames = ["text"],
            TopK = 10,
            VectorWeight = 0.1,
            TextWeight = 10.0,
        };
        await using MongoDBRAGProvider textHeavyProvider = new(
            client,
            databaseName!,
            collectionName,
            new RecordingEmbeddingGenerator { EmbeddingFactory = weightEmbeddingFactory },
            3,
            textHeavyOptions);
        try
        {
            await collection.InsertManyAsync(
            [
                new BsonDocument
                {
                    { "_id", tenantAId },
                    { "text", "Widgets ship in blue for tenant A." },
                    { "embedding", new BsonArray([1.0, 0.0, 0.0]) },
                    { "tenant_id", "tenant-a" },
                },
                new BsonDocument
                {
                    { "_id", tenantBId },
                    { "text", "Widgets also ship in blue for tenant B." },
                    { "embedding", new BsonArray([1.0, 0.0, 0.0]) },
                    { "tenant_id", "tenant-b" },
                },
                new BsonDocument
                {
                    { "_id", vectorMatchId },
                    // Embedding matches the query embedding exactly; text shares no terms with the query, so this
                    // document should only rank first under vector-dominant weighting.
                    { "text", "Completely unrelated shipping content about crates and pallets." },
                    { "embedding", new BsonArray([1.0, 0.0, 0.0]) },
                },
                new BsonDocument
                {
                    { "_id", textMatchId },
                    // Orthogonal embedding (zero cosine similarity to the query vector); text contains the exact
                    // query phrase, so this document should only rank first under text-dominant weighting.
                    { "text", $"This chunk contains the {weightQuery} verbatim for search matching." },
                    { "embedding", new BsonArray([0.0, 1.0, 0.0]) },
                },
            ]);

            await PollUntilSearchableAsync(
                vectorReadinessProvider,
                "blue widgets",
                results => results.Any(r => r.Id == tenantAId) && results.Any(r => r.Id == tenantBId),
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));
            await PollUntilSearchableAsync(
                textReadinessProvider,
                "blue widgets",
                results => results.Any(r => r.Id == tenantAId) && results.Any(r => r.Id == tenantBId),
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));

            IReadOnlyList<MongoDBRAGResult> results = await PollUntilSearchableAsync(
                hybridProvider,
                "blue widgets",
                tenantAId,
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));
            Assert.Contains(results, result => result.Id == tenantAId);
            Assert.DoesNotContain(results, result => result.Id == tenantBId);

            // RawDocument must preserve the complete original document against a real MongoDB deployment, and
            // neither the reserved score nor scoreDetails aliases may leak into it.
            MongoDBRAGResult tenantAResult = Assert.Single(results, result => result.Id == tenantAId);
            Assert.Equal("tenant-a", tenantAResult.RawDocument["tenant_id"].AsString);
            Assert.False(tenantAResult.RawDocument.Contains("_ragScore"));
            Assert.False(tenantAResult.RawDocument.Contains("_ragScoreDetails"));

            // Both weight-sensitive fixtures must be independently retrievable via *each* weighted provider (the
            // fused output is a union of both branches regardless of weight) before their top-ranked ordering is
            // asserted, so the ordering assertion below cannot pass vacuously because one fixture was never
            // fused into the results at all.
            IReadOnlyList<MongoDBRAGResult> vectorHeavyResults = await PollUntilSearchableAsync(
                vectorHeavyProvider,
                weightQuery,
                candidates => candidates.Any(r => r.Id == vectorMatchId) && candidates.Any(r => r.Id == textMatchId),
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));
            IReadOnlyList<MongoDBRAGResult> textHeavyResults = await PollUntilSearchableAsync(
                textHeavyProvider,
                weightQuery,
                candidates => candidates.Any(r => r.Id == vectorMatchId) && candidates.Any(r => r.Id == textMatchId),
                timeout: TimeSpan.FromSeconds(30),
                pollInterval: TimeSpan.FromSeconds(1));

            Assert.Equal(vectorMatchId, vectorHeavyResults[0].Id);
            Assert.Equal(textMatchId, textHeavyResults[0].Id);
        }
        finally
        {
            Assert.StartsWith("af_rag_dotnet_test_", prefix);
            await collection.DeleteManyAsync(
                Builders<BsonDocument>.Filter.In(
                    "_id",
                    new[] { tenantAId, tenantBId, vectorMatchId, textMatchId }));
        }
    }
}
