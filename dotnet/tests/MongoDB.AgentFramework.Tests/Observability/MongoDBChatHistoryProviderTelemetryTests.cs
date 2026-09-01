using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MongoDB.AgentFramework.Internal.Observability;
using MongoDB.AgentFramework.Tests.History;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Diagnostics;
using System.Net;

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>
/// Proves <see cref="MongoDBChatHistoryProvider"/>'s meaningful public operations each emit exactly one
/// telemetry activity/log using only the authorized fields, that a sentinel secret embedded in a simulated
/// driver failure never reaches any log field/message or activity tag, and that cancellation is always
/// recorded as its own distinct outcome rather than a failure.
/// </summary>
public sealed class MongoDBChatHistoryProviderTelemetryTests
{
    private const string SentinelSecret = "SENTINEL-SECRET-6a1c9f3e2b7d4a80b5e1c3f9a7d2e4b6";

    [Fact]
    public async Task SaveMessagesAsync_OnSuccess_RecordsPersistOutcomeAndCount()
    {
        var state = new HistoryCollectionState();
        var logger = new RecordingLogger<MongoDBChatHistoryProvider>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBChatHistoryProvider provider = CreateProvider(state, logger: logger);

        await provider.SaveMessagesAsync("session", [new ChatMessage(ChatRole.User, "hello")]);

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryFeature.History, activity.GetTagItem("feature"));
        Assert.Equal(MongoDBTelemetryOperation.Persist, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Dictionary<string, object?> fields = log.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(MongoDBTelemetryOperation.Persist, fields["operation"]);
        Assert.Equal(MongoDBTelemetryOutcome.Success, fields["outcome"]);
    }

    [Fact]
    public async Task SaveMessagesAsync_WithEmptyBatch_RecordsEmptyOutcome()
    {
        var state = new HistoryCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBChatHistoryProvider provider = CreateProvider(state);

        await provider.SaveMessagesAsync("session", []);

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task GetMessagesAsync_OnSuccess_RecordsLoadOutcomeAndCount()
    {
        var state = new HistoryCollectionState();
        MongoDBChatHistoryProvider seeder = CreateProvider(state);
        await seeder.SaveMessagesAsync("session", [new ChatMessage(ChatRole.User, "hi")]);
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBChatHistoryProvider provider = CreateProvider(state);

        IReadOnlyList<ChatMessage> messages = await provider.GetMessagesAsync("session");

        Assert.Single(messages);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Load, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task GetMessagesAsync_WithNoMessages_RecordsEmptyOutcome()
    {
        var state = new HistoryCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBChatHistoryProvider provider = CreateProvider(state);

        await provider.GetMessagesAsync("session");

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task ClearMessagesAsync_RecordsDeleteOutcomeAndCount()
    {
        var state = new HistoryCollectionState();
        MongoDBChatHistoryProvider seeder = CreateProvider(state);
        await seeder.SaveMessagesAsync("session", [new ChatMessage(ChatRole.User, "hi")]);
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBChatHistoryProvider provider = CreateProvider(state);

        await provider.ClearMessagesAsync("session");

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Delete, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task EnsureIndexesAsync_RecordsEnsureIndexOperationAndOmitsIndexName()
    {
        var state = new HistoryCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBChatHistoryProvider provider = CreateProvider(state);

        await provider.EnsureIndexesAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.EnsureIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.All(activity.TagObjects, tag => Assert.NotEqual("index_name", tag.Key));
    }

    [Fact]
    public async Task SaveMessagesAsync_WhenDriverThrowsWithSentinelSecret_NeverLeaksSecretAndClassifiesPersistence()
    {
        var state = new HistoryCollectionState { InsertException = OfflineException(SentinelSecret) };
        var logger = new RecordingLogger<MongoDBChatHistoryProvider>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBChatHistoryProvider provider = CreateProvider(state, logger: logger);

        await Assert.ThrowsAsync<MongoDBPersistenceException>(
            () => provider.SaveMessagesAsync("session", [new ChatMessage(ChatRole.User, "hello")]));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
        Assert.Equal("persistence", activity.GetTagItem("error_category"));
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
    public async Task GetMessagesAsync_WhenCanceled_RecordsCancelledOutcomeDistinctFromFailed()
    {
        var state = new HistoryCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBChatHistoryProvider provider = CreateProvider(state);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetMessagesAsync("session", cts.Token));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, activity.GetTagItem("outcome"));
        Assert.Null(activity.GetTagItem("error_category"));
    }

    private static MongoDBChatHistoryProvider CreateProvider(
        HistoryCollectionState state,
        ILogger<MongoDBChatHistoryProvider>? logger = null) =>
        new(HistoryCollectionProxy.Create(state), ValidOptions(), logger);

    private static MongoDBChatHistoryProviderOptions ValidOptions() =>
        new()
        {
            ApplicationId = "app",
            AgentId = "agent",
            SessionId = "session",
        };

    private static MongoConnectionException OfflineException(string message) =>
        new(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            message);
}
