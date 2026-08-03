using MongoDB.AgentFramework;
using MongoDB.Driver;

// This sample demonstrates the least-privilege separation docs/spec/features/index-management.md and ADR 0006
// describe: a "provisioner" identity (deployment-time tooling, typically running with the createSearchIndexes/
// dropSearchIndexes/updateSearchIndexes privileges) explicitly creates and waits for indexes to become queryable,
// while a distinct "runtime" identity (the identity MongoDBMemoryProvider/MongoDBRAGProvider actually connect
// with in production) only ever validates -- it never creates, updates, or drops anything. Two separate
// MongoDBMemoryIndexManager/MongoDBRAGIndexManager instances play these two roles below; in a real deployment
// they would typically also use two different connection strings/identities, not just two instances of the same
// client.
string uri = Environment.GetEnvironmentVariable("MONGODB_URI")
    ?? throw new InvalidOperationException("Set MONGODB_URI.");
string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")
    ?? throw new InvalidOperationException("Set MONGODB_DATABASE.");
string memoryCollectionName = Environment.GetEnvironmentVariable("MONGODB_MEMORY_COLLECTION")
    ?? "agent_framework_memories";
string ragCollectionName = Environment.GetEnvironmentVariable("MONGODB_RAG_COLLECTION")
    ?? "agent_framework_rag_chunks";

using var client = new MongoClient(uri);

var memoryDefinition = new MongoDBVectorSearchIndexDefinition(
    indexName: "agent_framework_memory",
    vectorFieldName: "content_embedding",
    vectorDimensions: 3,
    similarity: "cosine",
    filterFieldPaths: ["application_id", "agent_id", "user_id", "session_id"]);

var ragVectorDefinition = new MongoDBVectorSearchIndexDefinition(
    indexName: "agent_framework_rag_vector",
    vectorFieldName: "embedding",
    vectorDimensions: 3,
    similarity: "cosine",
    filterFieldPaths: ["tenant_id"]);

var ragSearchDefinition = new MongoDBSearchIndexDefinition(
    indexName: "agent_framework_rag_search",
    textFieldNames: ["text"],
    mandatoryFilter: MongoDBRAGFilter.Equal("tenant_id", "quickstart"));

Console.WriteLine("== Provisioner role: Ensure + WaitUntilReady (deployment-time identity) ==");
await using (var memoryProvisioner = new MongoDBMemoryIndexManager(
    client, databaseName, memoryCollectionName, memoryDefinition))
await using (var ragProvisioner = new MongoDBRAGIndexManager(
    client, databaseName, ragCollectionName, ragVectorDefinition, ragSearchDefinition))
{
    MongoDBIndexInfo memoryIndex = await memoryProvisioner.EnsureIndexAsync(
        waitUntilReady: true,
        timeout: TimeSpan.FromMinutes(2));
    Console.WriteLine(
        $"  Memory index '{memoryIndex.Name}': status={memoryIndex.Status}, queryable={memoryIndex.Queryable}");

    await ragProvisioner.EnsureHybridAsync(waitUntilReady: true, timeout: TimeSpan.FromMinutes(2));
    Console.WriteLine("  RAG vector + search indexes are both READY/queryable.");

    Console.WriteLine();
    Console.WriteLine("== Runtime role: Validate-only (the identity MongoDBMemoryProvider/MongoDBRAGProvider use) ==");
    await using var memoryRuntime = new MongoDBMemoryIndexManager(
        client, databaseName, memoryCollectionName, memoryDefinition);
    await using var ragRuntime = new MongoDBRAGIndexManager(
        client, databaseName, ragCollectionName, ragVectorDefinition, ragSearchDefinition);

    MongoDBIndexComparison memoryComparison = await memoryRuntime.ValidateIndexAsync();
    Console.WriteLine(
        $"  Memory index compatible: {memoryComparison.IsCompatible} " +
        $"(compatible differences: {memoryComparison.CompatibleDifferences.Count})");

    await ragRuntime.ValidateHybridAsync();
    Console.WriteLine("  RAG vector + search indexes are both compatible with their configured definitions.");

    Console.WriteLine();
    Console.WriteLine("== Cleanup: Drop (provisioner role only) ==");
    await memoryProvisioner.DropIndexAsync();
    await ragProvisioner.DropVectorSearchIndexAsync();
    await ragProvisioner.DropSearchIndexAsync();
    Console.WriteLine("  Dropped all three indexes.");
}
