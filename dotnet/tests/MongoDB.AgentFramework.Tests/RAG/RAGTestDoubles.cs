using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
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
