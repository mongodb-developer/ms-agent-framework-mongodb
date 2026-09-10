using Microsoft.Agents.AI.Workflows;
using MongoDB.AgentFramework;
using System.Text.Json;

string uri = Environment.GetEnvironmentVariable("MONGODB_URI") ??
    throw new InvalidOperationException("Set MONGODB_URI.");
string database = Environment.GetEnvironmentVariable("MONGODB_DATABASE") ??
    throw new InvalidOperationException("Set MONGODB_DATABASE.");
string collection = Environment.GetEnvironmentVariable("MONGODB_CHECKPOINT_COLLECTION") ??
    "workflow_checkpoints";
string workflowId = Environment.GetEnvironmentVariable("MONGODB_CHECKPOINT_WORKFLOW_ID") ??
    "checkpoint-quickstart-workflow";
string sessionId = Environment.GetEnvironmentVariable("MONGODB_CHECKPOINT_SESSION_ID") ??
    "checkpoint-quickstart-run";

// The pagination continuation-token signing key is a required, server-held secret (never derived from a
// token's own contents) that must stay stable and identical across every MongoDBCheckpointStore instance
// that must accept each other's tokens (for example every replica of a horizontally scaled service).
// Generate one with `openssl rand -base64 32` or `[Convert]::ToBase64String((New-Object byte[] 32 |
// ForEach-Object { [System.Security.Cryptography.RandomNumberGenerator]::Fill($_); $_ }))`, store it in a
// secret manager or protected environment variable, and never hard-code it in source.
string signingKeyBase64 = Environment.GetEnvironmentVariable("MONGODB_CHECKPOINT_SIGNING_KEY") ??
    throw new InvalidOperationException(
        "Set MONGODB_CHECKPOINT_SIGNING_KEY to a base64-encoded, at least 32-byte cryptographically random " +
        "secret (for example: openssl rand -base64 32). This key signs and validates pagination continuation " +
        "tokens and must never be a source-controlled literal.");
byte[] signingKey = Convert.FromBase64String(signingKeyBase64);

await using var store = new MongoDBCheckpointStore(
    uri,
    database,
    collection,
    new MongoDBCheckpointStoreOptions
    {
        TenantId = Environment.GetEnvironmentVariable("MONGODB_CHECKPOINT_TENANT_ID"),
        WorkflowId = workflowId,
        ContinuationTokenSigningKey = signingKey,
        DefaultExpiration = TimeSpan.FromDays(30),
    });

// EnsureIndexesAsync requires a deployment that supports multi-document transactions (a replica set,
// sharded cluster, or mongos) -- SaveCheckpointAsync/CreateCheckpointAsync use a transaction to allocate
// each checkpoint's monotonic sequence number atomically with its write. Against a standalone mongod this
// throws MongoDBCapabilityException rather than silently giving up the ordering guarantee.
await store.EnsureIndexesAsync();

// CheckpointManager.CreateJson accepts any ICheckpointStore<JsonElement>: this is the real, public
// Microsoft.Agents.AI.Workflows manager type wrapping MongoDBCheckpointStore, proving the store is a drop-in
// JsonCheckpointStore rather than a custom substitute.
CheckpointManager manager = CheckpointManager.CreateJson(store);
Console.WriteLine($"CheckpointManager created over MongoDBCheckpointStore: {manager}.");

// Simulate a small workflow run: root -> step -> a pending-approval branch point -> resumed-after-approval.
CheckpointInfo root = await store.CreateCheckpointAsync(
    sessionId,
    JsonSerializer.SerializeToElement(new { step = "start", pending_approval = false }));
Console.WriteLine($"Committed root checkpoint '{root.CheckpointId}'.");

CheckpointInfo running = await store.CreateCheckpointAsync(
    sessionId,
    JsonSerializer.SerializeToElement(new { step = "processing", pending_approval = false }),
    root);

CheckpointInfo pendingApproval = await store.CreateCheckpointAsync(
    sessionId,
    JsonSerializer.SerializeToElement(new { step = "awaiting_manager_approval", pending_approval = true }),
    running);
Console.WriteLine($"Committed pending-approval checkpoint '{pendingApproval.CheckpointId}'.");

// ... time passes; the workflow host process may even restart here before approval arrives ...

CheckpointInfo approved = await store.CreateCheckpointAsync(
    sessionId,
    JsonSerializer.SerializeToElement(new { step = "approved_and_completed", pending_approval = false }),
    pendingApproval);

// Resume: find the head of the lineage and reload its exact payload through the framework hook.
IEnumerable<CheckpointInfo> index = await store.RetrieveIndexAsync(sessionId);
CheckpointInfo latest = index.Last();
JsonElement resumedPayload = await store.RetrieveCheckpointAsync(sessionId, latest);
Console.WriteLine(
    $"Resumed at checkpoint '{latest.CheckpointId}', step='{resumedPayload.GetProperty("step").GetString()}'.");

// The richer facade exposes sequence/lineage/expiry metadata the raw framework contract does not.
MongoDBCheckpointRecord? latestRecord = await store.GetLatestCheckpointAsync(sessionId);
Console.WriteLine(
    $"Latest record: sequence={latestRecord?.Sequence}, parent='{latestRecord?.ParentCheckpointId}', " +
    $"expiresAt={latestRecord?.ExpiresAt}.");

MongoDBCheckpointPage page = await store.ListCheckpointsAsync(sessionId, limit: 10);
foreach (MongoDBCheckpointSummary summary in page.Items)
{
    Console.WriteLine($"  checkpoint '{summary.CheckpointId}' (sequence {summary.Sequence}).");
}

if (string.Equals(
    Environment.GetEnvironmentVariable("MONGODB_CHECKPOINT_CLEAR"),
    "true",
    StringComparison.OrdinalIgnoreCase))
{
    foreach (MongoDBCheckpointSummary summary in page.Items)
    {
        await store.DeleteCheckpointAsync(sessionId, summary.CheckpointId);
    }

    Console.WriteLine($"Deleted {page.Items.Count} checkpoint(s) for session '{sessionId}'.");
}
