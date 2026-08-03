using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using System.Collections;
using System.Net;
using System.Reflection;

namespace MongoDB.AgentFramework.Tests.Memory;

internal sealed class RecordingEmbeddingGenerator :
    IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string[]> Calls { get; } = [];

    public bool Cancel { get; set; }

    public TimeSpan Delay { get; set; }

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

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        string[] inputs = values.ToArray();
        Calls.Add(inputs);
        return new GeneratedEmbeddings<Embedding<float>>(
            inputs.Select(static value => new Embedding<float>(
                value.Contains("blue", StringComparison.OrdinalIgnoreCase)
                    ? new float[] { 1, 0, 0 }
                    : new float[] { 0, 1, 0 })));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class MemoryCollectionState
{
    private readonly object _attemptLock = new();

    /// <summary>Guards <see cref="SearchIndexes"/> mutation so concurrent Ensure calls race deterministically.</summary>
    public object SearchIndexLock { get; } = new();

    public List<BsonDocument> Inserted { get; } = [];

    public List<BsonDocument> AggregateStages { get; } = [];

    public List<BsonDocument> Results { get; set; } = [];

    public List<BsonDocument> ListedDocuments { get; set; } = [];

    public BsonDocument? DeleteFilter { get; set; }

    public Exception? InsertException { get; set; }

    public Func<IReadOnlyList<BsonDocument>, CancellationToken, Task>? InsertHandler { get; set; }

    public List<BsonDocument[]> InsertAttempts { get; } = [];

    public Exception? AggregateException { get; set; }

    public long DeletedCount { get; set; } = 1;

    public bool DeleteAcknowledged { get; set; } = true;

    public List<BsonDocument> SearchIndexes { get; set; } = [];

    public Queue<List<BsonDocument>> SearchIndexSnapshots { get; } = [];

    public CreateSearchIndexModel? CreatedSearchIndex { get; set; }

    public int CreateOneCallCount { get; set; }

    public Exception? CreateException { get; set; }

    public string? DroppedIndexName { get; set; }

    public int DropOneCallCount { get; set; }

    public Exception? DropException { get; set; }

    public string? UpdatedIndexName { get; set; }

    public BsonDocument? UpdatedDefinition { get; set; }

    public int UpdateCallCount { get; set; }

    public Exception? UpdateException { get; set; }

    public Exception? ListException { get; set; }

    public int ListCallCount { get; set; }

    public void CaptureAttempt(BsonDocument[] documents)
    {
        lock (_attemptLock)
        {
            InsertAttempts.Add(documents);
        }
    }

    public void CaptureSuccess(IEnumerable<BsonDocument> documents)
    {
        lock (_attemptLock)
        {
            Inserted.AddRange(documents);
        }
    }
}

internal class MemoryCollectionProxy : DispatchProxy
{
    public MemoryCollectionState State { get; set; } = null!;

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
                SearchIndexManagerProxy>();
            ((SearchIndexManagerProxy)(object)manager).State = State;
            return manager;
        }

        if (method == "InsertManyAsync")
        {
            BsonDocument[] documents = ((IEnumerable<BsonDocument>)args![0]!)
                .Select(static document => document.DeepClone().AsBsonDocument)
                .ToArray();
            State.CaptureAttempt(documents);
            if (State.InsertException is not null)
            {
                return Task.FromException(State.InsertException);
            }

            if (State.InsertHandler is not null)
            {
                return InvokeInsertHandlerAsync(State, documents, (CancellationToken)args[^1]!);
            }

            State.CaptureSuccess(documents);
            return Task.CompletedTask;
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

        if (method == "DeleteManyAsync")
        {
            var filter = (FilterDefinition<BsonDocument>)args![0]!;
            State.DeleteFilter = filter.Render(
                new RenderArgs<BsonDocument>(
                    BsonDocumentSerializer.Instance,
                    BsonSerializer.SerializerRegistry));
            DeleteResult result = State.DeleteAcknowledged
                ? new AcknowledgedDeleteResult(State.DeletedCount)
                : new UnacknowledgedDeleteResult();
            return Task.FromResult(result);
        }

        if (method == "FindAsync")
        {
            return Task.FromResult<IAsyncCursor<BsonDocument>>(
                new ListCursor<BsonDocument>(State.ListedDocuments));
        }

        throw new NotSupportedException($"Unexpected collection call: {targetMethod}");
    }

    public static IMongoCollection<BsonDocument> Create(
        MemoryCollectionState state)
    {
        var collection =
            DispatchProxy.Create<IMongoCollection<BsonDocument>, MemoryCollectionProxy>();
        ((MemoryCollectionProxy)(object)collection).State = state;
        return collection;
    }

    private static async Task InvokeInsertHandlerAsync(
        MemoryCollectionState state,
        BsonDocument[] documents,
        CancellationToken cancellationToken)
    {
        await state.InsertHandler!(documents, cancellationToken);
        state.CaptureSuccess(documents);
    }
}

internal class SearchIndexManagerProxy : DispatchProxy
{
    public MemoryCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "ListAsync")
        {
            State.ListCallCount++;
            if (State.ListException is not null)
            {
                return Task.FromException<IAsyncCursor<BsonDocument>>(State.ListException);
            }

            if (State.SearchIndexSnapshots.Count > 0)
            {
                State.SearchIndexes = State.SearchIndexSnapshots.Dequeue();
            }

            return Task.FromResult<IAsyncCursor<BsonDocument>>(
                new ListCursor<BsonDocument>(State.SearchIndexes));
        }

        if (targetMethod.Name == "CreateOneAsync" &&
            args![0] is CreateSearchIndexModel model)
        {
            lock (State.SearchIndexLock)
            {
                State.CreateOneCallCount++;
                if (State.CreateException is not null)
                {
                    return Task.FromException<string>(State.CreateException);
                }

                if (State.SearchIndexes.Any(index => index.GetValue("name", "").AsString == model.Name))
                {
                    // A concurrent caller already won the race to create this index; the real server would
                    // reject this second attempt as "already exists".
                    return Task.FromException<string>(
                        MemoryIndexFixtures.CommandException(68, "IndexAlreadyExists", "Index already exists"));
                }

                State.CreatedSearchIndex = model;
                State.SearchIndexes =
                [
                    .. State.SearchIndexes,
                    new BsonDocument
                    {
                        { "name", model.Name },
                        { "type", "vectorSearch" },
                        { "status", "READY" },
                        { "queryable", true },
                        { "latestDefinition", model.Definition },
                    },
                ];
                return Task.FromResult(model.Name);
            }
        }

        if (targetMethod.Name == "DropOneAsync")
        {
            State.DropOneCallCount++;
            State.DroppedIndexName = (string)args![0]!;
            return State.DropException is not null
                ? Task.FromException(State.DropException)
                : Task.CompletedTask;
        }

        if (targetMethod.Name == "UpdateAsync")
        {
            State.UpdateCallCount++;
            State.UpdatedIndexName = (string)args![0]!;
            State.UpdatedDefinition = (BsonDocument)args[1]!;
            return State.UpdateException is not null
                ? Task.FromException(State.UpdateException)
                : Task.CompletedTask;
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
/// A minimal <see cref="IMongoClient"/> test double built the same way as <see cref="MemoryCollectionProxy"/>: a
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

internal sealed class AcknowledgedDeleteResult(long count) : DeleteResult
{
    public override bool IsAcknowledged => true;

    public override long DeletedCount => count;
}

internal sealed class UnacknowledgedDeleteResult : DeleteResult
{
    public override bool IsAcknowledged => false;

    public override long DeletedCount =>
        throw new NotSupportedException("The delete was not acknowledged.");
}

/// <summary>
/// Builds fake index management fixtures shared by <see cref="MongoDBMemoryIndexAndOwnershipTests"/> and
/// <see cref="MongoDBMemoryIndexManagerTests"/>.
/// </summary>
internal static class MemoryIndexFixtures
{
    /// <summary>
    /// Builds a fake <see cref="MongoCommandException"/> as the driver would surface a failed search-index
    /// management command, with <paramref name="code"/>/<paramref name="codeName"/>/<paramref name="errorMessage"/>
    /// driving the exception's corresponding properties -- used to prove privilege/deployment-error recognition
    /// without a real deployment.
    /// </summary>
    public static MongoCommandException CommandException(int code, string codeName, string errorMessage)
    {
        Assembly assembly = typeof(MongoCommandException).Assembly;
        Type clusterIdType = assembly.GetTypes().First(t => t.Name == "ClusterId");
        Type serverIdType = assembly.GetTypes().First(t => t.Name == "ServerId");
        Type connectionIdType = assembly.GetTypes().First(t => t.Name == "ConnectionId");
        object clusterId = Activator.CreateInstance(clusterIdType)!;
        object serverId = Activator.CreateInstance(serverIdType, clusterId, new DnsEndPoint("localhost", 27017))!;
        object connectionId = Activator.CreateInstance(connectionIdType, serverId)!;
        var command = new BsonDocument("createSearchIndexes", "test");
        var result = new BsonDocument
        {
            { "ok", 0 },
            { "code", code },
            { "codeName", codeName },
            { "errmsg", errorMessage },
        };
        return (MongoCommandException)Activator.CreateInstance(
            typeof(MongoCommandException), connectionId, "command failed", command, result)!;
    }

    /// <summary>Builds a valid Vector Search index document matching <see cref="MongoDBVectorSearchIndexDefinition"/> defaults used across facade tests.</summary>
    public static BsonDocument ValidVectorIndex(
        string indexName = "facade_vector",
        string vectorFieldName = "embedding",
        int dimensions = 3,
        string status = "READY",
        bool queryable = true,
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
            { "status", status },
            { "queryable", queryable },
            { "latestDefinition", new BsonDocument("fields", fields) },
        };
    }
}
