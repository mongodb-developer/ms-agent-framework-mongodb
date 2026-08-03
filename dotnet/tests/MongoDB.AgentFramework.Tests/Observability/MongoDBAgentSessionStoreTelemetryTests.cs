using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MongoDB.AgentFramework.Internal.Observability;
using MongoDB.AgentFramework.Tests.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

#pragma warning disable MAAI001

namespace MongoDB.AgentFramework.Tests.Observability;

/// <summary>
/// Proves <see cref="MongoDBAgentSessionStore"/>'s meaningful public operations each emit exactly one telemetry
/// activity/log using only the authorized fields, that a sentinel secret embedded in a simulated driver failure
/// never reaches any log field/message or activity tag, and that cancellation is always recorded as its own
/// distinct outcome rather than a failure.
/// </summary>
public sealed class MongoDBAgentSessionStoreTelemetryTests
{
    private const string SentinelSecret = "SENTINEL-SECRET-9d4e2a71f8c63b0a95d7e1f4a2b8c6d0";

    [Fact]
    public async Task CreateAsync_OnSuccess_RecordsPersistOutcomeAndCount()
    {
        var state = new SessionCollectionState();
        var logger = new RecordingLogger<MongoDBAgentSessionStore>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state, logger: logger);

        await store.CreateAsync("session-1", new TestSession(), new FakeSessionAgent());

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryFeature.SessionStore, activity.GetTagItem("feature"));
        Assert.Equal(MongoDBTelemetryOperation.Persist, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));

        RecordedLogEntry log = Assert.Single(logger.Entries);
        Dictionary<string, object?> fields = log.State.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(MongoDBTelemetryOperation.Persist, fields["operation"]);
        Assert.Equal(MongoDBTelemetryOutcome.Success, fields["outcome"]);
    }

    [Fact]
    public async Task SetAsync_OnSuccess_RecordsPersistOutcomeAndCount()
    {
        var state = new SessionCollectionState();
        MongoDBAgentSessionStore seeder = CreateStore(state);
        var agent = new FakeSessionAgent();
        await seeder.CreateAsync("session-2", new TestSession(), agent);
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);

        await store.SetAsync("session-2", new TestSession(), agent);

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Persist, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task GetAsync_OnSuccess_RecordsLoadOutcomeAndCount()
    {
        var state = new SessionCollectionState();
        MongoDBAgentSessionStore seeder = CreateStore(state);
        var agent = new FakeSessionAgent();
        await seeder.CreateAsync("session-3", new TestSession(), agent);
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);

        MongoDBAgentSessionRecord? record = await store.GetAsync("session-3", agent);

        Assert.NotNull(record);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Load, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task GetAsync_WhenAbsent_RecordsEmptyOutcome()
    {
        var state = new SessionCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);

        MongoDBAgentSessionRecord? record = await store.GetAsync("missing-session", new FakeSessionAgent());

        Assert.Null(record);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task DeleteAsync_OnSuccess_RecordsDeleteOutcomeAndCount()
    {
        var state = new SessionCollectionState();
        MongoDBAgentSessionStore seeder = CreateStore(state);
        await seeder.CreateAsync("session-4", new TestSession(), new FakeSessionAgent());
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);

        bool deleted = await store.DeleteAsync("session-4");

        Assert.True(deleted);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.Delete, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task DeleteAsync_WhenAbsent_RecordsEmptyOutcome()
    {
        var state = new SessionCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);

        bool deleted = await store.DeleteAsync("missing-session");

        Assert.False(deleted);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task ListAsync_OnSuccess_RecordsListOutcomeAndCount()
    {
        var state = new SessionCollectionState();
        MongoDBAgentSessionStore seeder = CreateStore(state);
        await seeder.CreateAsync("session-5", new TestSession(), new FakeSessionAgent());
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);

        MongoDBAgentSessionPage page = await store.ListAsync(10);

        Assert.Single(page.Items);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.List, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.Equal(1, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task ListAsync_WhenEmpty_RecordsEmptyOutcome()
    {
        var state = new SessionCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);

        MongoDBAgentSessionPage page = await store.ListAsync(10);

        Assert.Empty(page.Items);
        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Empty, activity.GetTagItem("outcome"));
        Assert.Equal(0, activity.GetTagItem("result_count"));
    }

    [Fact]
    public async Task EnsureIndexesAsync_RecordsEnsureIndexOperationAndOmitsIndexName()
    {
        var state = new SessionCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);

        await store.EnsureIndexesAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.EnsureIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
        Assert.All(activity.TagObjects, tag => Assert.NotEqual("index_name", tag.Key));
    }

    [Fact]
    public async Task ValidateIndexesAsync_RecordsValidateIndexOperation()
    {
        var state = new SessionCollectionState();
        MongoDBAgentSessionStore seeder = CreateStore(state);
        await seeder.EnsureIndexesAsync();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);

        await store.ValidateIndexesAsync();

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOperation.ValidateIndex, activity.GetTagItem("operation"));
        Assert.Equal(MongoDBTelemetryOutcome.Success, activity.GetTagItem("outcome"));
    }

    [Fact]
    public async Task CreateAsync_WhenDriverThrowsWithSentinelSecret_NeverLeaksSecretAndClassifiesFailure()
    {
        var state = new SessionCollectionState { InsertException = OfflineException(SentinelSecret) };
        var logger = new RecordingLogger<MongoDBAgentSessionStore>();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state, logger: logger);

        await Assert.ThrowsAsync<MongoConnectionException>(
            () => store.CreateAsync("session-6", new TestSession(), new FakeSessionAgent()));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Failed, activity.GetTagItem("outcome"));
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
    public async Task GetAsync_WhenCanceled_RecordsCancelledOutcomeDistinctFromFailed()
    {
        var state = new SessionCollectionState();
        using var activities = new ActivityCapture(MongoDBTelemetry.ActivitySourceName);
        using var scope = new TelemetryTestScope();
        MongoDBAgentSessionStore store = CreateStore(state);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetAsync("session-7", new FakeSessionAgent(), cancellationToken: cts.Token));

        Activity activity = Assert.Single(activities.StoppedUnder(scope));
        Assert.Equal(MongoDBTelemetryOutcome.Cancelled, activity.GetTagItem("outcome"));
        Assert.Null(activity.GetTagItem("error_category"));
    }

    private static MongoDBAgentSessionStore CreateStore(
        SessionCollectionState state,
        ILogger<MongoDBAgentSessionStore>? logger = null) =>
        new(
            SessionCollectionProxy.Create(state),
            new MongoDBAgentSessionStoreOptions { ApplicationId = "app", AgentId = "agent" },
            logger);

    private static MongoConnectionException OfflineException(string message) =>
        new(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            message);

    private sealed class TestSession : AgentSession
    {
        public TestSession()
        {
        }
    }

    private sealed class FakeSessionAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(session.StateBag.Serialize());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedSession,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
