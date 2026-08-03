using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;
using System.Reflection;

namespace MongoDB.AgentFramework.Tests.Persistence;

internal sealed class SessionCollectionState
{
    private readonly object _gate = new();

    public List<BsonDocument> Documents { get; } = [];

    public List<CreateIndexModel<BsonDocument>> CreatedIndexes { get; } = [];

    public Exception? InsertException { get; set; }

    public T Locked<T>(Func<T> action)
    {
        lock (_gate)
        {
            return action();
        }
    }
}

internal sealed class SessionFakeMongoClientState
{
    public Exception? GetDatabaseException { get; set; }

    public int DisposeCount { get; set; }
}

/// <summary>
/// A minimal <see cref="IMongoClient"/> test double supporting only the members exercised by
/// <see cref="MongoDBAgentSessionStore"/>'s owned-client construction path (<c>GetDatabase</c> and
/// <c>Dispose</c>), used to prove the owned client is disposed if a later validation/connection step fails
/// during construction.
/// </summary>
internal class SessionFakeMongoClientProxy : DispatchProxy
{
    public SessionFakeMongoClientState State { get; set; } = null!;

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

    public static IMongoClient Create(SessionFakeMongoClientState state)
    {
        var client = DispatchProxy.Create<IMongoClient, SessionFakeMongoClientProxy>();
        ((SessionFakeMongoClientProxy)(object)client).State = state;
        return client;
    }
}

internal class SessionCollectionProxy : DispatchProxy
{
    public SessionCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod!.Name)
        {
            case "get_DocumentSerializer":
                return BsonDocumentSerializer.Instance;
            case "get_Settings":
                return new MongoCollectionSettings();
            case "get_Indexes":
                var manager = DispatchProxy.Create<IMongoIndexManager<BsonDocument>, SessionIndexManagerProxy>();
                ((SessionIndexManagerProxy)(object)manager).State = State;
                return manager;
            case "FindAsync":
                return FindAsync(args!);
            case "FindOneAndUpdateAsync":
                return FindOneAndUpdateAsync(args!);
            case "InsertOneAsync":
                return InsertOneAsync(args!);
            case "DeleteOneAsync":
                return DeleteOneAsync(args!);
            default:
                throw new NotSupportedException($"Unexpected collection call: {targetMethod}");
        }
    }

    public static IMongoCollection<BsonDocument> Create(SessionCollectionState state)
    {
        var collection = DispatchProxy.Create<IMongoCollection<BsonDocument>, SessionCollectionProxy>();
        ((SessionCollectionProxy)(object)collection).State = state;
        return collection;
    }

    private Task<IAsyncCursor<BsonDocument>> FindAsync(object?[] args)
    {
        BsonDocument filter = Render((FilterDefinition<BsonDocument>)args[0]!);
        var options = (FindOptions<BsonDocument, BsonDocument>)args[1]!;
        IEnumerable<BsonDocument> values = State.Locked(() =>
            State.Documents.Where(document => Matches(document, filter))
                .Select(static document => document.DeepClone().AsBsonDocument)
                .ToArray());
        if (options.Sort is not null)
        {
            BsonDocument sort = options.Sort.Render(
                new RenderArgs<BsonDocument>(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry));
            BsonElement element = sort.GetElement(0);
            values = element.Value.AsInt32 < 0
                ? values.OrderByDescending(document => document[element.Name])
                : values.OrderBy(document => document[element.Name]);
        }

        if (options.Limit is { } limit)
        {
            values = values.Take(limit);
        }

        return Task.FromResult<IAsyncCursor<BsonDocument>>(new SessionCursor(values.ToArray()));
    }

    private Task<BsonDocument?> FindOneAndUpdateAsync(object?[] args)
    {
        BsonDocument filter = Render((FilterDefinition<BsonDocument>)args[0]!);
        BsonDocument update = ((UpdateDefinition<BsonDocument>)args[1]!).Render(
            new RenderArgs<BsonDocument>(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry))
            .AsBsonDocument;
        var options = (FindOneAndUpdateOptions<BsonDocument, BsonDocument>)args[2]!;
        return Task.FromResult(State.Locked<BsonDocument?>(() =>
        {
            BsonDocument? document = State.Documents.FirstOrDefault(item => Matches(item, filter));
            bool isInsert = document is null;
            if (document is null)
            {
                if (options.IsUpsert != true)
                {
                    return null;
                }

                document = new BsonDocument();
                foreach (BsonElement element in filter)
                {
                    if (element.Value is not BsonDocument)
                    {
                        document[element.Name] = element.Value;
                    }
                }

                State.Documents.Add(document);
            }

            if (update.TryGetValue("$set", out BsonValue setOps))
            {
                foreach (BsonElement element in setOps.AsBsonDocument)
                {
                    document[element.Name] = element.Value;
                }
            }

            if (update.TryGetValue("$inc", out BsonValue incOps))
            {
                foreach (BsonElement element in incOps.AsBsonDocument)
                {
                    long current = document.TryGetValue(element.Name, out BsonValue existing)
                        ? existing.ToInt64()
                        : 0L;
                    document[element.Name] = current + element.Value.ToInt64();
                }
            }

            if (isInsert && update.TryGetValue("$setOnInsert", out BsonValue setOnInsertOps))
            {
                foreach (BsonElement element in setOnInsertOps.AsBsonDocument)
                {
                    document[element.Name] = element.Value;
                }
            }

            return document.DeepClone().AsBsonDocument;
        }));
    }

    private Task InsertOneAsync(object?[] args)
    {
        var document = ((BsonDocument)args[0]!).DeepClone().AsBsonDocument;
        if (State.InsertException is not null)
        {
            throw State.InsertException;
        }

        return Task.Run(() => State.Locked(() =>
        {
            if (State.Documents.Any(item => item["_id"] == document["_id"]))
            {
                throw DuplicateKeyException();
            }

            State.Documents.Add(document);
            return true;
        }));
    }

    internal static MongoCommandException DuplicateKeyException()
    {
        var connectionId = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        return new MongoCommandException(
            connectionId,
            "insert",
            new BsonDocument(),
            new BsonDocument
            {
                { "ok", 0 },
                { "code", 11000 },
                { "errmsg", "duplicate" },
            });
    }

    private Task<DeleteResult> DeleteOneAsync(object?[] args)
    {
        BsonDocument filter = Render((FilterDefinition<BsonDocument>)args[0]!);
        return Task.FromResult<DeleteResult>(State.Locked(() =>
        {
            int index = State.Documents.FindIndex(document => Matches(document, filter));
            if (index >= 0)
            {
                State.Documents.RemoveAt(index);
            }

            return new SessionDeleteResult(index >= 0 ? 1 : 0);
        }));
    }

    private static BsonDocument Render(FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry));

    private static bool Matches(BsonDocument document, BsonDocument filter)
    {
        foreach (BsonElement element in filter)
        {
            BsonValue actual = document.TryGetValue(element.Name, out BsonValue value) ? value : BsonNull.Value;
            if (element.Value is BsonDocument operation)
            {
                if (operation.TryGetValue("$gt", out BsonValue gt) && actual.CompareTo(gt) <= 0)
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
}

internal class SessionIndexManagerProxy : DispatchProxy
{
    public SessionCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "CreateManyAsync")
        {
            var models = ((IEnumerable<CreateIndexModel<BsonDocument>>)args![0]!).ToArray();
            State.CreatedIndexes.AddRange(models);
            return Task.FromResult<IEnumerable<string>>(models.Select(static model => model.Options.Name!));
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
                            new RenderArgs<BsonDocument>(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry))
                    },
                    { "unique", model.Options.Unique ?? false },
                    {
                        "partialFilterExpression",
                        model.Options.PartialFilterExpression is null
                            ? BsonNull.Value
                            : model.Options.PartialFilterExpression.Render(
                                new RenderArgs<BsonDocument>(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry))
                    },
                    {
                        "expireAfterSeconds",
                        model.Options.ExpireAfter is { } ttl ? (BsonValue)ttl.TotalSeconds : BsonNull.Value
                    },
                }).ToArray();
            return Task.FromResult<IAsyncCursor<BsonDocument>>(new SessionCursor(indexes));
        }

        throw new NotSupportedException($"Unexpected index call: {targetMethod}");
    }
}

internal sealed class SessionCursor(IReadOnlyList<BsonDocument> values) : IAsyncCursor<BsonDocument>
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

internal sealed class SessionDeleteResult(long count) : DeleteResult
{
    public override bool IsAcknowledged => true;

    public override long DeletedCount => count;
}
