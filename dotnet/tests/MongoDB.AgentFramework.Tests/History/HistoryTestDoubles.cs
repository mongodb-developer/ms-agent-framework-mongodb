using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using System.Reflection;

namespace MongoDB.AgentFramework.Tests.History;

internal sealed class HistoryCollectionState
{
    private readonly object _gate = new();

    public List<BsonDocument> Documents { get; } = [];

    public BsonDocument? LastFindFilter { get; set; }

    public BsonDocument? LastFindSort { get; set; }

    public int? LastFindLimit { get; set; }

    public List<CreateIndexModel<BsonDocument>> CreatedIndexes { get; } = [];

    public int OperationCount { get; set; }

    public Exception? Failure { get; set; }

    public Exception? InsertException { get; set; }

    public Func<BsonDocument, CancellationToken, Task>? InsertHandler { get; set; }

    public List<BsonDocument> InsertAttempts { get; } = [];

    public async Task<T> LockedAsync<T>(Func<T> action)
    {
        await Task.Yield();
        lock (_gate)
        {
            return action();
        }
    }
}

internal class HistoryCollectionProxy : DispatchProxy
{
    public HistoryCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        State.OperationCount++;
        if (State.Failure is not null)
        {
            return FailedTask(targetMethod!.ReturnType, State.Failure);
        }

        switch (targetMethod!.Name)
        {
            case "get_DocumentSerializer":
                return BsonDocumentSerializer.Instance;
            case "get_Settings":
                return new MongoCollectionSettings();
            case "get_Indexes":
                var manager = DispatchProxy.Create<
                    IMongoIndexManager<BsonDocument>,
                    HistoryIndexManagerProxy>();
                ((HistoryIndexManagerProxy)(object)manager).State = State;
                return manager;
            case "FindAsync":
                return FindAsync(args!);
            case "FindOneAndUpdateAsync":
                return FindOneAndUpdateAsync(args!);
            case "InsertOneAsync":
                return InsertOneAsync(args!);
            case "DeleteManyAsync":
                return DeleteManyAsync(args!);
            case "DeleteOneAsync":
                return DeleteOneAsync(args!);
            default:
                throw new NotSupportedException($"Unexpected collection call: {targetMethod}");
        }
    }

    public static IMongoCollection<BsonDocument> Create(HistoryCollectionState state)
    {
        var collection =
            DispatchProxy.Create<IMongoCollection<BsonDocument>, HistoryCollectionProxy>();
        ((HistoryCollectionProxy)(object)collection).State = state;
        return collection;
    }

    private Task<IAsyncCursor<BsonDocument>> FindAsync(object?[] args)
    {
        BsonDocument filter = Render((FilterDefinition<BsonDocument>)args[0]!);
        var options = (FindOptions<BsonDocument, BsonDocument>)args[1]!;
        State.LastFindFilter = filter;
        State.LastFindSort = options.Sort?.Render(
            new RenderArgs<BsonDocument>(
                BsonDocumentSerializer.Instance,
                BsonSerializer.SerializerRegistry));
        State.LastFindLimit = options.Limit;
        IEnumerable<BsonDocument> values = State.Documents
            .Where(document => Matches(document, filter))
            .Select(static document => document.DeepClone().AsBsonDocument);
        if (State.LastFindSort is { ElementCount: > 0 } sort)
        {
            BsonElement element = sort.GetElement(0);
            values = element.Value.AsInt32 < 0
                ? values.OrderByDescending(document => document[element.Name])
                : values.OrderBy(document => document[element.Name]);
        }

        if (options.Limit is { } limit)
        {
            values = values.Take(limit);
        }

        return Task.FromResult<IAsyncCursor<BsonDocument>>(
            new HistoryCursor(values.ToArray()));
    }

    private Task<BsonDocument> FindOneAndUpdateAsync(object?[] args)
    {
        BsonDocument filter = Render((FilterDefinition<BsonDocument>)args[0]!);
        BsonDocument update = ((UpdateDefinition<BsonDocument>)args[1]!).Render(
            new RenderArgs<BsonDocument>(
                BsonDocumentSerializer.Instance,
                BsonSerializer.SerializerRegistry)).AsBsonDocument;
        return State.LockedAsync(() =>
        {
            BsonDocument? document = State.Documents.FirstOrDefault(item => Matches(item, filter));
            if (document is null)
            {
                document = filter.DeepClone().AsBsonDocument;
                document.Remove("_kind");
                document["_kind"] = "sequence";
                document["sequence"] = 0L;
                if (update.TryGetValue("$setOnInsert", out BsonValue setOnInsert))
                {
                    document.AddRange(setOnInsert.AsBsonDocument);
                }

                State.Documents.Add(document);
            }

            document["sequence"] = document["sequence"].ToInt64() +
                update["$inc"]["sequence"].ToInt64();
            return document.DeepClone().AsBsonDocument;
        });
    }

    private async Task InsertOneAsync(object?[] args)
    {
        var document = ((BsonDocument)args[0]!).DeepClone().AsBsonDocument;
        var cancellationToken = (CancellationToken)args[^1]!;
        bool isMessage = document.GetValue("_kind", "") == "message";
        if (isMessage)
        {
            State.InsertAttempts.Add(document.DeepClone().AsBsonDocument);
        }

        if (isMessage && State.InsertException is not null)
        {
            throw State.InsertException;
        }

        if (isMessage && State.InsertHandler is not null)
        {
            await State.InsertHandler(document.DeepClone().AsBsonDocument, cancellationToken);
        }

        await State.LockedAsync(() =>
        {
            if (State.Documents.Any(item => item["_id"] == document["_id"]))
            {
                throw new InvalidOperationException("duplicate test document");
            }

            State.Documents.Add(document);
            return true;
        });
    }

    private Task<DeleteResult> DeleteManyAsync(object?[] args)
    {
        BsonDocument filter = Render((FilterDefinition<BsonDocument>)args[0]!);
        int before = State.Documents.Count;
        State.Documents.RemoveAll(document => Matches(document, filter));
        return Task.FromResult<DeleteResult>(
            new HistoryDeleteResult(before - State.Documents.Count));
    }

    private Task<DeleteResult> DeleteOneAsync(object?[] args)
    {
        BsonDocument filter = Render((FilterDefinition<BsonDocument>)args[0]!);
        int index = State.Documents.FindIndex(document => Matches(document, filter));
        if (index >= 0)
        {
            State.Documents.RemoveAt(index);
        }

        return Task.FromResult<DeleteResult>(new HistoryDeleteResult(index >= 0 ? 1 : 0));
    }

    private static BsonDocument Render(FilterDefinition<BsonDocument> filter) =>
        filter.Render(
            new RenderArgs<BsonDocument>(
                BsonDocumentSerializer.Instance,
                BsonSerializer.SerializerRegistry));

    private static bool Matches(BsonDocument document, BsonDocument filter)
    {
        foreach (BsonElement element in filter)
        {
            if (!document.TryGetValue(element.Name, out BsonValue actual))
            {
                return false;
            }

            if (element.Value is BsonDocument operation &&
                operation.TryGetValue("$gte", out BsonValue minimum))
            {
                if (actual.CompareTo(minimum) < 0)
                {
                    return false;
                }
            }
            else if (actual != element.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static object FailedTask(Type returnType, Exception exception)
    {
        if (returnType == typeof(Task))
        {
            return Task.FromException(exception);
        }

        Type valueType = returnType.GenericTypeArguments[0];
        return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
            .MakeGenericMethod(valueType)
            .Invoke(null, [exception])!;
    }
}

internal class HistoryIndexManagerProxy : DispatchProxy
{
    public HistoryCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "CreateManyAsync")
        {
            var models = ((IEnumerable<CreateIndexModel<BsonDocument>>)args![0]!).ToArray();
            State.CreatedIndexes.AddRange(models);
            return Task.FromResult<IEnumerable<string>>(
                models.Select(static model => model.Options.Name!));
        }

        if (targetMethod.Name == "ListAsync")
        {
            BsonDocument[] indexes = State.CreatedIndexes.Select(model =>
                new BsonDocument
                {
                    { "name", model.Options.Name },
                    {
                        "key",
                        model.Keys.Render(
                            new RenderArgs<BsonDocument>(
                                BsonDocumentSerializer.Instance,
                                BsonSerializer.SerializerRegistry))
                    },
                    { "unique", model.Options.Unique ?? false },
                    {
                        "partialFilterExpression",
                        model.Options.PartialFilterExpression is null
                            ? BsonNull.Value
                            : model.Options.PartialFilterExpression.Render(
                                new RenderArgs<BsonDocument>(
                                    BsonDocumentSerializer.Instance,
                                    BsonSerializer.SerializerRegistry))
                    },
                    {
                        "expireAfterSeconds",
                        model.Options.ExpireAfter is { } ttl
                            ? (BsonValue)ttl.TotalSeconds
                            : BsonNull.Value
                    },
                }).ToArray();
            return Task.FromResult<IAsyncCursor<BsonDocument>>(new HistoryCursor(indexes));
        }

        throw new NotSupportedException($"Unexpected index call: {targetMethod}");
    }
}

internal sealed class HistoryCursor(IReadOnlyList<BsonDocument> values) :
    IAsyncCursor<BsonDocument>
{
    private bool _moved;

    public IEnumerable<BsonDocument> Current { get; private set; } = [];

    public bool MoveNext(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Current = _moved ? [] : values;
        return !_moved && (_moved = true);
    }

    public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(MoveNext(cancellationToken));

    public void Dispose()
    {
    }
}

internal sealed class HistoryDeleteResult(long count) : DeleteResult
{
    public override bool IsAcknowledged => true;

    public override long DeletedCount => count;
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
/// A minimal <see cref="IMongoClient"/> test double built the same way as <see cref="HistoryCollectionProxy"/>: a
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
