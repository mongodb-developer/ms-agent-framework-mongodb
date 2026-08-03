using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MongoDB.AgentFramework.Internal.Observability;
using MongoDB.AgentFramework.Tests.Memory;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Diagnostics;
using System.Net;

#pragma warning disable MAAI001

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>
/// Proves <see cref="MongoDBMemoryProvider"/>'s meaningful public operations each emit exactly one telemetry
/// activity/log using only the authorized fields, that a sentinel secret embedded in a simulated driver
/// failure never reaches any log field/message or activity tag, and that cancellation is always recorded as
/// its own distinct outcome rather than a failure.
/// </summary>
public sealed class MongoDBMemoryProviderTelemetryTests
{
    private const string SentinelSecret = "SENTINEL-SECRET-9d3b7f2c4a1e4b6c8f0a2d5e7b9c1f3a";

    [Fact]
    public async Task StoreAsync_OnSuccess_RecordsPersistOutcomeAndCount()
    {
        var state = new MemoryCollectionState();
        var logger = new RecordingLogger<MongoDBMemoryProvider>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryProvider provider = CreateProvider(state, logger: logger);

        await provider.StoreAsync(
            [new ChatMessage(ChatRole.User, "blue preference")],
            new MongoDBMemoryScope(userId: "u"));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryFeature.Memory, activity.GetTagItem("feature"));
        Assert.Equal(MongoDBTelemetryOperation.Persist, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Dictionary<string, object?> fields = log.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(MongoDBTelemetryOperation.Persist, fields["operation"]);
        Assert.Equal(MongoDBTelemetryOutcome.Success, fields["outcome"]);
        Assert.Equal(1, fields["result_count"]);
    }

    [Fact]
    public async Task SearchAsync_OnSuccess_RecordsRetrieveOutcomeModeAndCandidateBucket()
    {
        var state = new MemoryCollectionState
        {
            Results =
            [
                new BsonDocument
                {
                    { "_id", "m1" }, { "role", "user" }, { "content", "blue" },
                    { "score", 0.9 },
                },
            ],
        };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryProvider provider = CreateProvider(state);

        IReadOnlyList<MongoDBMemorySearchResult> results = await provider.SearchAsync(
            "blue", new MongoDBMemoryScope(userId: "u"));

        Assert.Single(results);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Retrieve, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryMode.Ann, activity.GetTagItem("mode"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
        Assert.NotNull(activity.GetTagItem("candidate_bucket"));
    }

    [Fact]
    public async Task SearchAsync_WithNoMatches_RecordsEmptyOutcome()
    {
        var state = new MemoryCollectionState { Results = [] };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryProvider provider = CreateProvider(state);

        await provider.SearchAsync("blue", new MongoDBMemoryScope(userId: "u"));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task SearchAsync_WhenDriverThrowsWithSentinelSecret_NeverLeaksSecretAndClassifiesRetrieval()
    {
        var state = new MemoryCollectionState { AggregateException = OfflineException(SentinelSecret) };
        var logger = new RecordingLogger<MongoDBMemoryProvider>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryProvider provider = CreateProvider(state, logger: logger);

        await Assert.ThrowsAsync<MongoDBRetrievalException>(
            () => provider.SearchAsync("blue", new MongoDBMemoryScope(userId: "u")));

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
        var state = new MemoryCollectionState();
        var embeddings = new RecordingEmbeddingGenerator { Cancel = true };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryProvider provider = CreateProvider(state, embeddings);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SearchAsync("blue", new MongoDBMemoryScope(userId: "u"), cancellationToken: cts.Token));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, activity.GetTagItem("outcome"));
        Assert.Null(activity.GetTagItem("error_category"));
    }

    [Fact]
    public async Task DeleteByIdAsync_RecordsDeleteOutcomeAndCount()
    {
        var state = new MemoryCollectionState { DeletedCount = 1 };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryProvider provider = CreateProvider(state);

        await provider.DeleteByIdAsync("m1", new MongoDBMemoryScope(userId: "u"));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Delete, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task ListAsync_RecordsListOutcomeAndCount()
    {
        var state = new MemoryCollectionState
        {
            ListedDocuments =
            [
                new BsonDocument { { "_id", "m1" }, { "role", "user" }, { "created_at", DateTime.UtcNow } },
            ],
        };
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryProvider provider = CreateProvider(state);

        await provider.ListAsync(new MongoDBMemoryScope(userId: "u"));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.List, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task EnsureVectorSearchIndexAsync_RecordsEnsureIndexOperationAndOmitsIndexName()
    {
        var state = new MemoryCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBMemoryProvider provider = CreateProvider(state);

        await provider.EnsureVectorSearchIndexAsync();

        Activity activity = activities.StoppedUnder(scope)[0];
        Assert.Equal(MongoDBTelemetryOperation.EnsureIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.All(activity.TagObjects, tag => Assert.NotEqual("index_name", tag.Key));
    }

    private static MongoDBMemoryProvider CreateProvider(
        MemoryCollectionState state,
        RecordingEmbeddingGenerator? embeddings = null,
        ILogger<MongoDBMemoryProvider>? logger = null) =>
        new(
            MemoryCollectionProxy.Create(state),
            embeddings ?? new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(new MongoDBMemoryScope(userId: "user")),
            options: null,
            logger: logger);

    private static MongoConnectionException OfflineException(string message) =>
        new(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            message);
}
