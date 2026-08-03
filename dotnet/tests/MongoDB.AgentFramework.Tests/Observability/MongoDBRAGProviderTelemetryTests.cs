using Microsoft.Extensions.Logging;
using MongoDB.AgentFramework.Internal.Observability;
using MongoDB.AgentFramework.Tests.RAG;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Diagnostics;
using System.Net;

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>
/// Proves <see cref="MongoDBRAGProvider"/>'s meaningful public operations each emit exactly one telemetry
/// activity/log with the authorized fields, that Hybrid's internal capability pre-check records its own
/// distinct <c>validate_index</c> operation rather than being folded into <c>retrieve</c>, that a sentinel
/// secret embedded in a simulated driver failure never leaks, and that cancellation is recorded distinctly.
/// </summary>
public sealed class MongoDBRAGProviderTelemetryTests
{
    private const string SentinelSecret = "SENTINEL-SECRET-4e1a9c7b3d5f4a2e8b6c0d9f1a3e5b7c";

    [Fact]
    public async Task SearchAsync_WithVectorAnn_RecordsRetrieveAnnModeAndCandidateBucket()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "c1" }, { "text", "chunk" }, { "_ragScore", 0.5 } }],
        };
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn, NumCandidates = 50 };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGProvider provider = CreateProvider(state, options: options);

        await provider.SearchAsync("query");

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryFeature.Rag, activity.GetTagItem("feature"));
        Assert.Equal(MongoDBTelemetryOperation.Retrieve, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryMode.Ann, activity.GetTagItem("mode"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
        Assert.Equal("11-100", activity.GetTagItem("candidate_bucket"));
    }

    [Fact]
    public async Task SearchAsync_WithFullText_RecordsFullTextModeAndNoCandidateBucket()
    {
        var state = new RAGCollectionState { Results = [] };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGProvider provider = CreateFullTextProvider(state);

        await provider.SearchAsync("query");

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryMode.FullText, activity.GetTagItem("mode"));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
        Assert.Null(activity.GetTagItem("candidate_bucket"));
    }

    [Fact]
    public async Task SearchAsync_WithHybridRrf_RecordsDistinctRetrieveAndValidateIndexActivities()
    {
        var state = new RAGCollectionState
        {
            Results = [new BsonDocument { { "_id", "c1" }, { "text", "chunk" }, { "_ragScore", 0.5 } }],
            SearchIndexes = [RAGIndexFixtures.ValidVectorIndex(), RAGIndexFixtures.ValidSearchIndex()],
            BuildInfoResult = new BsonDocument("version", "8.0.0"),
        };
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.HybridRrf };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGProvider provider = CreateProvider(state, options: options);

        await provider.SearchAsync("query");

        IReadOnlyList<Activity> mine = activities.StoppedUnder(scope);
        Assert.Contains(mine, a => a.GetTagItem("operation") as string == MongoDBTelemetryOperation.Retrieve
            && a.GetTagItem("mode") as string == MongoDBTelemetryMode.HybridRrf);
        Assert.Contains(mine, a => a.GetTagItem("operation") as string == MongoDBTelemetryOperation.ValidateIndex);
    }

    [Fact]
    public async Task SearchAsync_WhenDriverThrowsWithSentinelSecret_NeverLeaksSecretAndClassifiesRetrieval()
    {
        var state = new RAGCollectionState { AggregateException = OfflineException(SentinelSecret) };
        var logger = new RecordingLogger<MongoDBRAGProvider>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGProvider provider = CreateProvider(state, logger: logger);

        await Assert.ThrowsAsync<MongoDBRetrievalException>(() => provider.SearchAsync("query"));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
        Assert.Equal("retrieval", activity.GetTagItem("error_category"));
        foreach (KeyValuePair<string, string?> tag in activity.TagObjects.Select(
            t => new KeyValuePair<string, string?>(t.Key, t.Value?.ToString())))
        {
            Assert.DoesNotContain(SentinelSecret, tag.Value ?? string.Empty, StringComparison.Ordinal);
        }

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Assert.DoesNotContain(SentinelSecret, log.Message, StringComparison.Ordinal);
        foreach (object? value in log.State.Select(pair => pair.Value))
        {
            Assert.DoesNotContain(SentinelSecret, value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SearchAsync_WhenCanceled_RecordsCancelledOutcomeDistinctFromFailed()
    {
        var state = new RAGCollectionState();
        var embeddings = new RecordingEmbeddingGenerator { Cancel = true };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGProvider provider = CreateProvider(state, embeddings);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SearchAsync("query", cts.Token));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, activity.GetTagItem("outcome"));
        Assert.Null(activity.GetTagItem("error_category"));
    }

    [Fact]
    public async Task ValidateSearchIndexAsync_RecordsValidateIndexOperationAndOmitsIndexName()
    {
        var state = new RAGCollectionState
        {
            SearchIndexes = [RAGIndexFixtures.ValidSearchIndex()],
        };
        var options = new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBRAGProvider provider = CreateFullTextProvider(state, options);

        await provider.ValidateSearchIndexAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.ValidateIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.All(activity.TagObjects, tag => Assert.NotEqual("index_name", tag.Key));
    }

    private static MongoDBRAGProvider CreateProvider(
        RAGCollectionState state,
        RecordingEmbeddingGenerator? embeddings = null,
        MongoDBRAGProviderOptions? options = null,
        ILogger<MongoDBRAGProvider>? logger = null) =>
        new(
            RAGCollectionProxy.Create(state),
            embeddings ?? new RecordingEmbeddingGenerator(),
            3,
            options ?? new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.VectorAnn },
            logger);

    private static MongoDBRAGProvider CreateFullTextProvider(
        RAGCollectionState state,
        MongoDBRAGProviderOptions? options = null,
        ILogger<MongoDBRAGProvider>? logger = null) =>
        new(
            RAGCollectionProxy.Create(state),
            options ?? new MongoDBRAGProviderOptions { SearchMode = MongoDBSearchMode.FullText },
            logger);

    private static MongoConnectionException OfflineException(string message) =>
        new(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            message);
}
