using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.AgentFramework;
using System.Runtime.CompilerServices;
using System.Text.Json;

#pragma warning disable MAAI001

string uri = Environment.GetEnvironmentVariable("MONGODB_URI") ??
    throw new InvalidOperationException("Set MONGODB_URI.");
string database = Environment.GetEnvironmentVariable("MONGODB_DATABASE") ??
    throw new InvalidOperationException("Set MONGODB_DATABASE.");
string collection = Environment.GetEnvironmentVariable("MONGODB_SESSION_COLLECTION") ??
    "agent_sessions";
string sessionId = Environment.GetEnvironmentVariable("MONGODB_SESSION_ID") ??
    "session-quickstart-session";

await using var store = new MongoDBAgentSessionStore(
    uri,
    database,
    collection,
    new MongoDBAgentSessionStoreOptions
    {
        ApplicationId = Environment.GetEnvironmentVariable("MONGODB_SESSION_APPLICATION_ID") ??
            "session-quickstart",
        AgentId = Environment.GetEnvironmentVariable("MONGODB_SESSION_AGENT_ID") ??
            "sample-agent",
        DefaultExpiration = TimeSpan.FromDays(30),
    });

await store.EnsureIndexesAsync();

// Microsoft.Agents.AI.Abstractions (verified at the pinned floor 1.13.0, unchanged through 1.16.0)
// does not publish a concrete AIAgent, nor a session-hosting persistence contract: AgentSession is
// serialized/deserialized only through the originating agent instance. DemoAgent below is the minimal
// stand-in required to exercise that public serialization surface end-to-end.
var agent = new DemoAgent();

var bag = new AgentSessionStateBag();
bag.SetValue("turn_count", (object)1);
bag.SetValue("last_message", "Hello from MongoDB Session Store.");

MongoDBAgentSessionRecord created = await store.CreateAsync(sessionId, new DemoSession(bag), agent);
Console.WriteLine($"Created session '{created.SessionId}' at version {created.Version}.");

MongoDBAgentSessionRecord? loaded = await store.GetAsync(sessionId, agent);
if (loaded is not null)
{
    Console.WriteLine(
        $"Reloaded turn_count={((JsonElement)loaded.Session.StateBag.GetValue<object>("turn_count")!).GetInt32()}, " +
        $"last_message={loaded.Session.StateBag.GetValue<string>("last_message")}");

    var updatedBag = new AgentSessionStateBag();
    updatedBag.SetValue("turn_count", (object)2);
    updatedBag.SetValue("last_message", "Second turn, same session.");
    MongoDBAgentSessionRecord updated = await store.SetAsync(
        sessionId,
        new DemoSession(updatedBag),
        agent,
        expectedVersion: loaded.Version);
    Console.WriteLine($"Updated session '{updated.SessionId}' to version {updated.Version}.");
}

MongoDBAgentSessionPage page = await store.ListAsync(10);
foreach (MongoDBAgentSessionSummary summary in page.Items)
{
    Console.WriteLine($"Listed session '{summary.SessionId}' (version {summary.Version}).");
}

if (string.Equals(
    Environment.GetEnvironmentVariable("MONGODB_SESSION_CLEAR"),
    "true",
    StringComparison.OrdinalIgnoreCase))
{
    bool deleted = await store.DeleteAsync(sessionId);
    Console.WriteLine($"Deleted session: {deleted}.");
}

// AgentSession is abstract with no framework-provided concrete type; a minimal subclass is required to
// hold the AgentSessionStateBag instance passed to the Session Store.
internal sealed class DemoSession : AgentSession
{
    public DemoSession()
    {
    }

    public DemoSession(AgentSessionStateBag stateBag)
        : base(stateBag)
    {
    }
}

// AIAgent has no framework-provided concrete implementation in Microsoft.Agents.AI.Abstractions; this
// minimal agent exists only to exercise the public SerializeSessionAsync/DeserializeSessionAsync surface
// that MongoDBAgentSessionStore is built on.
internal sealed class DemoAgent : AIAgent
{
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentSession>(new DemoSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(session.StateBag.Serialize());

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedSession,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentSession>(new DemoSession(AgentSessionStateBag.Deserialize(serializedSession)));

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("DemoAgent only demonstrates session persistence, not invocation.");

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
