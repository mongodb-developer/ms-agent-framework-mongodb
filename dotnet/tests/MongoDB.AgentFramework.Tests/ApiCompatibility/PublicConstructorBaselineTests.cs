using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.ApiCompatibility;

/// <summary>
/// Proves that every public constructor signature present before the observability/security instrumentation
/// slice (<c>feature/dornet-implementation</c> at commit <c>3d908a0</c>) still exists, byte-for-byte, alongside
/// any new logger-aware overload. Adding an <see cref="ILogger{TCategoryName}"/> parameter directly onto an
/// existing public constructor -- even as an optional parameter with a default value -- changes its CLR/IL
/// signature and breaks binary compatibility for any already-compiled caller: default argument values are
/// resolved at the *caller's* compile time, not at this callee's binary surface, so a compiled call site that
/// targets the original N-parameter constructor has no N-parameter constructor to bind to once a parameter is
/// added. This file is the regression gate for that requirement: telemetry-aware construction must always be
/// additive (a new sibling overload), never a modification of an existing one. See
/// docs/development/observability-security/dotnet-telemetry.md.
/// </summary>
public sealed class PublicConstructorBaselineTests
{
    private static bool HasExactPublicConstructor(Type type, params Type[] parameterTypes) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .SequenceEqual(parameterTypes));

    private static int PublicConstructorCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length;

    private static void AssertExactSignatures(Type type, IReadOnlyList<Type[]> expectedSignatures)
    {
        foreach (Type[] signature in expectedSignatures)
        {
            Assert.True(
                HasExactPublicConstructor(type, signature),
                $"{type.Name} is missing a public constructor with parameter types: " +
                string.Join(", ", signature.Select(t => t.Name)));
        }

        Assert.Equal(expectedSignatures.Count, PublicConstructorCount(type));
    }

    [Fact]
    public void ChatHistoryProviderExposesOriginalAndLoggerAwareConstructors()
    {
        Type collection = typeof(IMongoCollection<BsonDocument>);
        Type options = typeof(MongoDBChatHistoryProviderOptions);
        Type logger = typeof(ILogger<MongoDBChatHistoryProvider>);

        AssertExactSignatures(typeof(MongoDBChatHistoryProvider),
        [
            // Original (pre-observability) signatures: must never change.
            [collection, options],
            [typeof(IMongoDatabase), typeof(string), options],
            [typeof(IMongoClient), typeof(string), typeof(string), options],
            [typeof(string), typeof(string), typeof(string), options],
            // New sibling overloads with a required (non-optional) logger parameter.
            [collection, options, logger],
            [typeof(IMongoDatabase), typeof(string), options, logger],
            [typeof(IMongoClient), typeof(string), typeof(string), options, logger],
            [typeof(string), typeof(string), typeof(string), options, logger],
        ]);
    }

    [Fact]
    public void AgentSessionStoreExposesOriginalAndLoggerAwareConstructors()
    {
        Type collection = typeof(IMongoCollection<BsonDocument>);
        Type options = typeof(MongoDBAgentSessionStoreOptions);
        Type logger = typeof(ILogger<MongoDBAgentSessionStore>);

        AssertExactSignatures(typeof(MongoDBAgentSessionStore),
        [
            // Original (pre-observability) signatures: must never change.
            [collection, options],
            [typeof(IMongoDatabase), typeof(string), options],
            [typeof(IMongoClient), typeof(string), typeof(string), options],
            [typeof(string), typeof(string), typeof(string), options],
            // New sibling overloads with a required (non-optional) logger parameter.
            [collection, options, logger],
            [typeof(IMongoDatabase), typeof(string), options, logger],
            [typeof(IMongoClient), typeof(string), typeof(string), options, logger],
            [typeof(string), typeof(string), typeof(string), options, logger],
        ]);
    }

    [Fact]
    public void CheckpointStoreExposesOriginalAndLoggerAwareConstructors()
    {
        Type collection = typeof(IMongoCollection<BsonDocument>);
        Type options = typeof(MongoDBCheckpointStoreOptions);
        Type logger = typeof(ILogger<MongoDBCheckpointStore>);

        AssertExactSignatures(typeof(MongoDBCheckpointStore),
        [
            // Original (pre-observability) signatures: must never change.
            [collection, options],
            [typeof(IMongoDatabase), typeof(string), options],
            [typeof(IMongoClient), typeof(string), typeof(string), options],
            [typeof(string), typeof(string), typeof(string), options],
            // New sibling overloads with a required (non-optional) logger parameter.
            [collection, options, logger],
            [typeof(IMongoDatabase), typeof(string), options, logger],
            [typeof(IMongoClient), typeof(string), typeof(string), options, logger],
            [typeof(string), typeof(string), typeof(string), options, logger],
        ]);
    }

    /// <summary>
    /// Audit-only: <see cref="MongoDBMemoryProvider"/>'s public constructors already carried an optional
    /// <see cref="ILogger{TCategoryName}"/> parameter before this branch's observability work began (verified via
    /// <c>git show 3d908a0:...</c>), so no compatibility fix was needed here -- this guards against a future
    /// regression reintroducing the same class of break some other way.
    /// </summary>
    [Fact]
    public void MemoryProviderExposesItsOriginalFourConstructorShapes()
    {
        Type collection = typeof(IMongoCollection<BsonDocument>);
        Type database = typeof(IMongoDatabase);
        Type client = typeof(IMongoClient);
        Type embeddingGenerator = typeof(IEmbeddingGenerator<string, Embedding<float>>);
        Type stateFactory = typeof(Func<AgentSession?, MongoDBMemoryProvider.State>);
        Type options = typeof(MongoDBMemoryProviderOptions);
        Type logger = typeof(ILogger<MongoDBMemoryProvider>);

        AssertExactSignatures(typeof(MongoDBMemoryProvider),
        [
            [database, typeof(string), embeddingGenerator, typeof(int), stateFactory, options, logger],
            [collection, embeddingGenerator, typeof(int), stateFactory, options, logger],
            [client, typeof(string), typeof(string), embeddingGenerator, typeof(int), stateFactory, options, logger],
            [typeof(string), typeof(string), typeof(string), embeddingGenerator, typeof(int), stateFactory, options, logger],
        ]);
    }

    /// <summary>
    /// Audit-only: <see cref="MongoDBRAGProvider"/>'s public constructors already carried an optional
    /// <see cref="ILogger{TCategoryName}"/> parameter before this branch's observability work began (verified via
    /// <c>git show 3d908a0:...</c>), so no compatibility fix was needed here -- this guards against a future
    /// regression reintroducing the same class of break some other way.
    /// </summary>
    [Fact]
    public void RAGProviderExposesItsOriginalEightConstructorShapes()
    {
        Type collection = typeof(IMongoCollection<BsonDocument>);
        Type database = typeof(IMongoDatabase);
        Type client = typeof(IMongoClient);
        Type embeddingGenerator = typeof(IEmbeddingGenerator<string, Embedding<float>>);
        Type options = typeof(MongoDBRAGProviderOptions);
        Type logger = typeof(ILogger<MongoDBRAGProvider>);

        AssertExactSignatures(typeof(MongoDBRAGProvider),
        [
            // Vector-capable family (embedding generator + vector dimensions).
            [database, typeof(string), embeddingGenerator, typeof(int), options, logger],
            [collection, embeddingGenerator, typeof(int), options, logger],
            [client, typeof(string), typeof(string), embeddingGenerator, typeof(int), options, logger],
            [typeof(string), typeof(string), typeof(string), embeddingGenerator, typeof(int), options, logger],
            // FullText-only family (no embedding generator or vector dimensions).
            [database, typeof(string), options, logger],
            [collection, options, logger],
            [client, typeof(string), typeof(string), options, logger],
            [typeof(string), typeof(string), typeof(string), options, logger],
        ]);
    }
}
