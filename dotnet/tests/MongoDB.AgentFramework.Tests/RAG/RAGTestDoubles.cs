using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using System.Net;
using System.Reflection;

namespace MongoDB.AgentFramework.Tests.RAG;

internal sealed class RecordingEmbeddingGenerator :
    IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string[]> Calls { get; } = [];

    public bool Cancel { get; set; }

    public TimeSpan Delay { get; set; }

    public int Dimensions { get; set; } = 3;

    public Func<string, float[]>? EmbeddingFactory { get; set; }

    public Exception? FailWith { get; set; }

    public int ReturnedVectorCount { get; set; } = -1;

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Cancel)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (FailWith is { } failure)
        {
            throw failure;
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        string[] inputs = values.ToArray();
        Calls.Add(inputs);
        int count = ReturnedVectorCount >= 0 ? ReturnedVectorCount : inputs.Length;
        return new GeneratedEmbeddings<Embedding<float>>(
            Enumerable.Range(0, count).Select(index => new Embedding<float>(
                EmbeddingFactory is not null && index < inputs.Length
                    ? EmbeddingFactory(inputs[index])
                    : Enumerable.Repeat(0.1f, Dimensions).ToArray())));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class RAGCollectionState
{
    public List<BsonDocument> AggregateStages { get; } = [];

    public List<BsonDocument> Results { get; set; } = [];

    public Exception? AggregateException { get; set; }

    public List<BsonDocument> SearchIndexes { get; set; } = [];

    public Queue<List<BsonDocument>> SearchIndexSnapshots { get; } = [];

    public Exception? SearchIndexListException { get; set; }

    public int SearchIndexListCallCount { get; set; }

    /// <summary>The fake <c>buildInfo</c> command result used by the Hybrid server-version capability check.</summary>
    public BsonDocument BuildInfoResult { get; set; } = new("version", "8.0.0");

    public Exception? RunCommandException { get; set; }

    public int RunCommandCallCount { get; set; }
}

internal class RAGCollectionProxy : DispatchProxy
{
    public RAGCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        string method = targetMethod!.Name;
        if (method == "get_DocumentSerializer")
        {
            return BsonDocumentSerializer.Instance;
        }

        if (method == "get_Settings")
        {
            return new MongoCollectionSettings();
        }

        if (method == "get_SearchIndexes")
        {
            var manager = DispatchProxy.Create<
                MongoDB.Driver.Search.IMongoSearchIndexManager,
                RAGSearchIndexManagerProxy>();
            ((RAGSearchIndexManagerProxy)(object)manager).State = State;
            return manager;
        }

        if (method == "AggregateAsync")
        {
            if (State.AggregateException is not null)
            {
                Type resultType = targetMethod.ReturnType.GenericTypeArguments[0];
                return typeof(Task).GetMethod(
                    nameof(Task.FromException),
                    1,
                    [typeof(Exception)])!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [State.AggregateException]);
            }

            var pipeline = (PipelineDefinition<BsonDocument, BsonDocument>)args![0]!;
            RenderedPipelineDefinition<BsonDocument> rendered = pipeline.Render(
                new RenderArgs<BsonDocument>(
                    BsonDocumentSerializer.Instance,
                    BsonSerializer.SerializerRegistry));
            State.AggregateStages.AddRange(rendered.Documents);
            return Task.FromResult<IAsyncCursor<BsonDocument>>(
                new ListCursor<BsonDocument>(State.Results));
        }

        if (method == "get_Database")
        {
            return RAGDatabaseProxy.Create(State);
        }

        throw new NotSupportedException($"Unexpected collection call: {targetMethod}");
    }

    public static IMongoCollection<BsonDocument> Create(RAGCollectionState state)
    {
        var collection =
            DispatchProxy.Create<IMongoCollection<BsonDocument>, RAGCollectionProxy>();
        ((RAGCollectionProxy)(object)collection).State = state;
        return collection;
    }
}

/// <summary>
/// Fakes <see cref="IMongoDatabase.RunCommandAsync{TResult}"/> only, used by the Hybrid server-version capability
/// check (<c>buildInfo</c>). <see cref="RAGCollectionState.RunCommandCallCount"/> proves whether a bounded cache
/// avoided a repeated round trip.
/// </summary>
internal class RAGDatabaseProxy : DispatchProxy
{
    public RAGCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "RunCommandAsync")
        {
            State.RunCommandCallCount++;
            Type resultType = targetMethod.ReturnType.GenericTypeArguments[0];
            if (State.RunCommandException is not null)
            {
                return typeof(Task).GetMethod(
                    nameof(Task.FromException),
                    1,
                    [typeof(Exception)])!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [State.RunCommandException]);
            }

            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [State.BuildInfoResult]);
        }

        throw new NotSupportedException($"Unexpected database call: {targetMethod}");
    }

    public static IMongoDatabase Create(RAGCollectionState state)
    {
        var database = DispatchProxy.Create<IMongoDatabase, RAGDatabaseProxy>();
        ((RAGDatabaseProxy)(object)database).State = state;
        return database;
    }
}

/// <summary>
/// Fakes <see cref="MongoDB.Driver.Search.IMongoSearchIndexManager.ListAsync"/> only, mirroring the Memory test
/// double's <c>SearchIndexManagerProxy</c>: <see cref="RAGCollectionState.SearchIndexSnapshots"/> lets a test queue
/// successive result sets to simulate an index transitioning across repeated calls (for example, missing then
/// ready), and <see cref="RAGCollectionState.SearchIndexListCallCount"/> proves whether a bounded cache actually
/// avoided a network round trip.
/// </summary>
internal class RAGSearchIndexManagerProxy : DispatchProxy
{
    public RAGCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "ListAsync")
        {
            State.SearchIndexListCallCount++;
            if (State.SearchIndexListException is not null)
            {
                return Task.FromException<IAsyncCursor<BsonDocument>>(State.SearchIndexListException);
            }

            if (State.SearchIndexSnapshots.Count > 0)
            {
                State.SearchIndexes = State.SearchIndexSnapshots.Dequeue();
            }

            return Task.FromResult<IAsyncCursor<BsonDocument>>(
                new ListCursor<BsonDocument>(State.SearchIndexes));
        }

        throw new NotSupportedException($"Unexpected search-index call: {targetMethod}");
    }
}

/// <summary>
/// Tracks calls made to a <see cref="FakeMongoClientProxy"/>, used to prove a connection-string constructor
/// disposes its owned client if a step after client creation (for example resolving the database/collection)
/// throws.
/// </summary>
internal sealed class FakeMongoClientState
{
    public Exception? GetDatabaseException { get; set; }

    public int DisposeCount { get; set; }
}

/// <summary>
/// A minimal <see cref="IMongoClient"/> test double built the same way as <see cref="RAGCollectionProxy"/>: a
/// <see cref="DispatchProxy"/> only needs to handle the specific members exercised by production code
/// (<c>GetDatabase</c> and <c>Dispose</c>); every other member is intentionally unsupported.
/// </summary>
internal class FakeMongoClientProxy : DispatchProxy
{
    public FakeMongoClientState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        string method = targetMethod!.Name;
        if (method == "GetDatabase")
        {
            if (State.GetDatabaseException is not null)
            {
                throw State.GetDatabaseException;
            }

            throw new NotSupportedException("Fake client requires a configured GetDatabaseException.");
        }

        if (method == "Dispose")
        {
            State.DisposeCount++;
            return null;
        }

        throw new NotSupportedException($"Unexpected client call: {targetMethod}");
    }

    public static IMongoClient Create(FakeMongoClientState state)
    {
        var client = DispatchProxy.Create<IMongoClient, FakeMongoClientProxy>();
        ((FakeMongoClientProxy)(object)client).State = state;
        return client;
    }
}

/// <summary>
/// A settable clock used to deterministically test the bounded Search-index validation cache without waiting on
/// real time or a real network call.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class ListCursor<T>(IReadOnlyList<T> values) : IAsyncCursor<T>
{
    private bool _moved;

    public IEnumerable<T> Current { get; private set; } = [];

    public bool MoveNext(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_moved)
        {
            Current = [];
            return false;
        }

        _moved = true;
        Current = values;
        return true;
    }

    public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(MoveNext(cancellationToken));

    public void Dispose()
    {
    }
}

/// <summary>
/// An <see cref="IReadOnlyList{T}"/> that tolerates only a bounded number of enumerations before throwing,
/// simulating a caller-controlled collection that changes shape or becomes invalid across repeated reads. Used to
/// prove that construction never enumerates an options list a second time after an owned client already exists.
/// </summary>
internal sealed class SingleUseFieldNames(IReadOnlyList<string> values, int toleratedEnumerations) :
    IReadOnlyList<string>
{
    private int _enumerations;

    public int Count => values.Count;

    public string this[int index] => values[index];

    public IEnumerator<string> GetEnumerator()
    {
        _enumerations++;
        if (_enumerations > toleratedEnumerations)
        {
            throw new InvalidOperationException(
                $"This list was enumerated more than the tolerated {toleratedEnumerations} time(s).");
        }

        return values.GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Builds ready-to-use, <c>READY</c>/queryable Vector Search and Search index definitions matching the default
/// <see cref="MongoDBRAGProviderOptions"/> field/index names, shared by every test that exercises Hybrid's
/// capability-validation seam (directly or implicitly, once <c>SearchAsync</c> invokes it) so the shape of a valid
/// index definition is defined exactly once rather than duplicated per test class.
/// </summary>
internal static class RAGIndexFixtures
{
    /// <summary>
    /// Builds a Vector Search index definition. <paramref name="filterFieldPaths"/> adds additional
    /// <c>type: "filter"</c> fields (beyond the vector field itself), matching the mandatory-filter fields a test
    /// configures on <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>.
    /// </summary>
    public static BsonDocument ValidVectorIndex(
        string indexName = "agent_framework_rag_vector",
        string vectorFieldName = "embedding",
        int dimensions = 3,
        params string[] filterFieldPaths)
    {
        var fields = new BsonArray
        {
            new BsonDocument
            {
                { "type", "vector" },
                { "path", vectorFieldName },
                { "numDimensions", dimensions },
                { "similarity", "cosine" },
            },
        };
        fields.AddRange(filterFieldPaths.Select(
            path => new BsonDocument { { "type", "filter" }, { "path", path } }));
        return new BsonDocument
        {
            { "name", indexName },
            { "type", "vectorSearch" },
            { "status", "READY" },
            { "queryable", true },
            { "latestDefinition", new BsonDocument("fields", fields) },
        };
    }

    /// <summary>
    /// Builds a non-dynamic Search index definition mapping <paramref name="textFieldNames"/> as
    /// <c>"string"</c>, plus any additional <paramref name="filterFieldTypes"/> entries (field path to Atlas
    /// Search field type), matching the mandatory-filter fields a test configures on
    /// <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>.
    /// </summary>
    public static BsonDocument ValidSearchIndex(
        string indexName = "agent_framework_rag_search",
        IEnumerable<string>? textFieldNames = null,
        IReadOnlyDictionary<string, string>? filterFieldTypes = null)
    {
        var fields = new BsonDocument();
        foreach (string textField in textFieldNames ?? ["text"])
        {
            fields[textField] = new BsonDocument("type", "string");
        }

        foreach ((string path, string type) in filterFieldTypes ?? new Dictionary<string, string>())
        {
            fields[path] = new BsonDocument("type", type);
        }

        return new BsonDocument
        {
            { "name", indexName },
            { "type", "search" },
            { "status", "READY" },
            { "queryable", true },
            {
                "latestDefinition",
                new BsonDocument(
                    "mappings",
                    new BsonDocument { { "dynamic", false }, { "fields", fields } })
            },
        };
    }

    /// <summary>Builds a dynamic-mapping Search index definition, which indexes every field automatically.</summary>
    public static BsonDocument DynamicSearchIndex(string indexName = "agent_framework_rag_search") =>
        new()
        {
            { "name", indexName },
            { "type", "search" },
            { "status", "READY" },
            { "queryable", true },
            { "latestDefinition", new BsonDocument("mappings", new BsonDocument("dynamic", true)) },
        };

    /// <summary>
    /// Builds a fake <see cref="MongoCommandException"/> as the driver would surface a failed <c>aggregate</c>
    /// command, with <paramref name="code"/>/<paramref name="errorMessage"/> in the result document driving the
    /// exception's <see cref="MongoCommandException.Code"/>/<see cref="MongoCommandException.ErrorMessage"/>
    /// properties -- used to prove recognition of a <c>$rankFusion</c>-unsupported server response without a real
    /// deployment.
    /// </summary>
    public static MongoCommandException CommandException(int code, string errorMessage)
    {
        Assembly assembly = typeof(MongoCommandException).Assembly;
        Type clusterIdType = assembly.GetTypes().First(t => t.Name == "ClusterId");
        Type serverIdType = assembly.GetTypes().First(t => t.Name == "ServerId");
        Type connectionIdType = assembly.GetTypes().First(t => t.Name == "ConnectionId");
        object clusterId = Activator.CreateInstance(clusterIdType)!;
        object serverId = Activator.CreateInstance(serverIdType, clusterId, new DnsEndPoint("localhost", 27017))!;
        object connectionId = Activator.CreateInstance(connectionIdType, serverId)!;
        var command = new BsonDocument("aggregate", "test");
        var result = new BsonDocument
        {
            { "ok", 0 },
            { "code", code },
            { "codeName", "CommandFailed" },
            { "errmsg", errorMessage },
        };
        return (MongoCommandException)Activator.CreateInstance(
            typeof(MongoCommandException), connectionId, "command failed", command, result)!;
    }
}
