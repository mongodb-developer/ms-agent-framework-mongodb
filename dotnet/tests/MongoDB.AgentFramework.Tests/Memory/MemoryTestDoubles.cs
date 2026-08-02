using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using System.Collections;
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
            State.CreatedSearchIndex = model;
            return Task.FromResult(model.Name);
        }

        throw new NotSupportedException($"Unexpected search-index call: {targetMethod}");
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
