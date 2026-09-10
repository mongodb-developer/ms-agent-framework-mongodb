using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;

namespace MongoDB.AgentFramework.Tests.Persistence;

/// <summary>
/// A fixed, deterministic (never randomly regenerated) 32-byte test signing key satisfying
/// <see cref="MongoDBCheckpointStoreOptions.ContinuationTokenSigningKey"/>'s minimum-length requirement,
/// shared by every test that only needs *a* valid key rather than testing key validation itself. Deterministic
/// so token/signature assertions across test runs are reproducible; never used outside this test project.
/// </summary>
internal static class CheckpointStoreTestSigningKey
{
    public static byte[] Bytes { get; } = SHA256.HashData("mongodb-agentframework-checkpoint-store-tests"u8.ToArray());
}

internal sealed class CheckpointCollectionState
{
    private readonly object _gate = new();

    public List<BsonDocument> Documents { get; } = [];

    public List<CreateIndexModel<BsonDocument>> CreatedIndexes { get; } = [];

    public Exception? InsertException { get; set; }

    public Exception? FindException { get; set; }

    public Exception? DeleteException { get; set; }

    /// <summary>
    /// When set, every fake <c>FindAsync</c> call awaits this before returning -- used to prove
    /// <see cref="MongoDBCheckpointStoreOptions.PersistenceTimeout"/>/<c>RetrievalTimeout</c> is actually
    /// enforced (the delay observes the deadline-derived cancellation token, so it throws
    /// <see cref="OperationCanceledException"/> once the deadline elapses, exactly like a real hung driver
    /// call would).
    /// </summary>
    public Func<CancellationToken, Task>? FindDelay { get; set; }

    /// <summary>
    /// Shared transaction-serialization lock: <see cref="CheckpointFakeClientSessionHandleProxy"/>'s
    /// <c>WithTransactionAsync</c> holds this for the full duration of the callback, approximating real
    /// MongoDB's write-conflict-based serialization of concurrent transactions against the same per-session
    /// sequence-counter document -- enough to write deterministic interleaving tests without a real server.
    /// </summary>
    public object TransactionGate { get; } = new();

    public int TransactionAttempt { get; set; }

    /// <summary>
    /// Invoked synchronously, once per <c>WithTransactionAsync</c> attempt, while holding
    /// <see cref="TransactionGate"/> -- lets a test deterministically control interleaving (for example,
    /// blocking the first attempt until a second attempt has genuinely started and blocked on the gate).
    /// </summary>
    public Action<int>? BeforeTransactionBody { get; set; }

    private int _transactionCallCount;

    /// <summary>
    /// Assigns each <c>WithTransactionAsync</c> call a stable, thread-safe 1-based call index *before* it
    /// attempts to acquire <see cref="TransactionGate"/> -- lets a test deterministically identify "the second
    /// caller" and know it has reached the verge of the (potentially blocking) lock acquisition, independent of
    /// whether it actually contends.
    /// </summary>
    public int NextTransactionCallIndex() => Interlocked.Increment(ref _transactionCallCount);

    /// <summary>
    /// Invoked synchronously for every <c>WithTransactionAsync</c> call, immediately before it attempts to
    /// acquire <see cref="TransactionGate"/> (i.e. before any lock contention/blocking).
    /// </summary>
    public Action<int>? BeforeTransactionLockAcquire { get; set; }

    /// <summary>
    /// When set, <c>WithTransactionAsync</c> throws this immediately instead of running its callback --
    /// simulates a deployment (standalone <c>mongod</c>) that rejects transaction usage outright.
    /// </summary>
    public Exception? TransactionsUnsupportedException { get; set; }

    public T Locked<T>(Func<T> action)
    {
        lock (_gate)
        {
            return action();
        }
    }
}

internal sealed class CheckpointFakeMongoClientState
{
    public Exception? GetDatabaseException { get; set; }

    public int DisposeCount { get; set; }
}

/// <summary>
/// A minimal <see cref="IMongoClient"/> test double supporting only the members exercised by
/// <see cref="MongoDBCheckpointStore"/>'s owned-client construction path (<c>GetDatabase</c> and
/// <c>Dispose</c>), used to prove the owned client is disposed if a later validation/connection step fails
/// during construction.
/// </summary>
internal class CheckpointFakeMongoClientProxy : DispatchProxy
{
    public CheckpointFakeMongoClientState State { get; set; } = null!;

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

    public static IMongoClient Create(CheckpointFakeMongoClientState state)
    {
        var client = DispatchProxy.Create<IMongoClient, CheckpointFakeMongoClientProxy>();
        ((CheckpointFakeMongoClientProxy)(object)client).State = state;
        return client;
    }
}

/// <summary>
/// A minimal session-capable <see cref="IMongoClient"/> test double reachable via
/// <c>collection.Database.Client</c>, supporting only <c>StartSessionAsync</c> -- the sole client member the
/// rewritten transactional <c>SaveCheckpointCoreAsync</c> path exercises.
/// </summary>
internal class CheckpointFakeSessionClientProxy : DispatchProxy
{
    public CheckpointCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "StartSessionAsync")
        {
            var cancellationToken = args is [_, CancellationToken token] ? token : default;
            cancellationToken.ThrowIfCancellationRequested();
            var handle = DispatchProxy.Create<IClientSessionHandle, CheckpointFakeClientSessionHandleProxy>();
            var proxy = (CheckpointFakeClientSessionHandleProxy)(object)handle;
            proxy.State = State;
            proxy.ProxiedSelf = handle;
            return Task.FromResult(handle);
        }

        throw new NotSupportedException($"Unexpected session-capable client call: {targetMethod}");
    }

    public static IMongoClient Create(CheckpointCollectionState state)
    {
        var client = DispatchProxy.Create<IMongoClient, CheckpointFakeSessionClientProxy>();
        ((CheckpointFakeSessionClientProxy)(object)client).State = state;
        return client;
    }
}

/// <summary>
/// A minimal <see cref="IMongoDatabase"/> test double exposing only <c>Client</c>, reachable via
/// <c>collection.Database</c>.
/// </summary>
internal class CheckpointFakeDatabaseProxy : DispatchProxy
{
    public CheckpointCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "get_Client")
        {
            return CheckpointFakeSessionClientProxy.Create(State);
        }

        throw new NotSupportedException($"Unexpected database call: {targetMethod}");
    }

    public static IMongoDatabase Create(CheckpointCollectionState state)
    {
        var database = DispatchProxy.Create<IMongoDatabase, CheckpointFakeDatabaseProxy>();
        ((CheckpointFakeDatabaseProxy)(object)database).State = state;
        return database;
    }
}

/// <summary>
/// A minimal <see cref="IClientSessionHandle"/> test double supporting only <c>WithTransactionAsync</c> and
/// <c>Dispose</c>. Approximates real MongoDB transaction semantics against this fake's shared in-memory state:
/// the callback runs under <see cref="CheckpointCollectionState.TransactionGate"/> (serializing concurrent
/// "transactions" the same way a real transactional write conflict on the shared sequence-counter document
/// would), and any exception from the callback rolls back all document mutations made during it.
/// </summary>
internal class CheckpointFakeClientSessionHandleProxy : DispatchProxy
{
    public CheckpointCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "WithTransactionAsync")
        {
            return WithTransactionAsyncCore(args!);
        }

        if (targetMethod.Name == "Dispose")
        {
            return null;
        }

        throw new NotSupportedException($"Unexpected client session call: {targetMethod}");
    }

    private object WithTransactionAsyncCore(object?[] args)
    {
        var callback = (Delegate)args[0]!;
        var cancellationToken = args.Length > 2 && args[2] is CancellationToken token ? token : default;
        cancellationToken.ThrowIfCancellationRequested();

        object thisSession = ProxiedSelf!;
        int callIndex = State.NextTransactionCallIndex();
        State.BeforeTransactionLockAcquire?.Invoke(callIndex);
        lock (State.TransactionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State.TransactionsUnsupportedException is { } unsupported)
            {
                throw unsupported;
            }

            int attempt = ++State.TransactionAttempt;
            State.BeforeTransactionBody?.Invoke(attempt);

            List<BsonDocument> snapshot = State.Locked(() =>
                State.Documents.Select(static document => document.DeepClone().AsBsonDocument).ToList());
            object callbackResult = callback.DynamicInvoke(thisSession, cancellationToken)!;
            var callbackTask = (Task)callbackResult;
            try
            {
                callbackTask.GetAwaiter().GetResult();
            }
            catch
            {
                State.Locked(() =>
                {
                    State.Documents.Clear();
                    State.Documents.AddRange(snapshot);
                    return true;
                });
                throw;
            }

            return callbackResult;
        }
    }

    /// <summary>
    /// The interface-typed proxy instance wrapping this <see cref="DispatchProxy"/>, so the callback can be
    /// invoked with "this session" as its <c>IClientSessionHandle</c> argument, exactly as the real driver does.
    /// </summary>
    public object? ProxiedSelf { get; set; }
}

internal class CheckpointCollectionProxy : DispatchProxy
{
    public CheckpointCollectionState State { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod!.Name)
        {
            case "get_DocumentSerializer":
                return BsonDocumentSerializer.Instance;
            case "get_Settings":
                return new MongoCollectionSettings();
            case "get_Database":
                return CheckpointFakeDatabaseProxy.Create(State);
            case "get_Indexes":
                var manager = DispatchProxy.Create<IMongoIndexManager<BsonDocument>, CheckpointIndexManagerProxy>();
                ((CheckpointIndexManagerProxy)(object)manager).State = State;
                return manager;
            case "FindAsync":
                return FindAsync(StripSession(args!));
            case "FindOneAndUpdateAsync":
                return FindOneAndUpdateAsync(StripSession(args!));
            case "InsertOneAsync":
                return InsertOneAsync(StripSession(args!));
            case "DeleteOneAsync":
                return DeleteOneAsync(StripSession(args!));
            default:
                throw new NotSupportedException($"Unexpected collection call: {targetMethod}");
        }
    }

    /// <summary>
    /// The session-aware overloads of the collection members this store uses all place the
    /// <see cref="IClientSessionHandle"/> as the first parameter; this fake does not need to distinguish
    /// session-scoped calls from non-session ones (both share this fake's single in-memory state, and
    /// transactional serialization/rollback is already handled by
    /// <see cref="CheckpointFakeClientSessionHandleProxy"/>), so it simply strips a leading session argument to
    /// normalize both overloads onto the same handling.
    /// </summary>
    private static object?[] StripSession(object?[] args) =>
        args is [IClientSessionHandle, .. object?[] rest] ? rest : args;

    public static IMongoCollection<BsonDocument> Create(CheckpointCollectionState state)
    {
        var collection = DispatchProxy.Create<IMongoCollection<BsonDocument>, CheckpointCollectionProxy>();
        ((CheckpointCollectionProxy)(object)collection).State = state;
        return collection;
    }

    private async Task<IAsyncCursor<BsonDocument>> FindAsync(object?[] args)
    {
        BsonDocument filter = Render((FilterDefinition<BsonDocument>)args[0]!);
        var options = (FindOptions<BsonDocument, BsonDocument>)args[1]!;
        var cancellationToken = args.Length > 2 && args[2] is CancellationToken token ? token : default;
        if (State.FindDelay is { } delay)
        {
            await delay(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (State.FindException is not null)
        {
            throw State.FindException;
        }

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

        return new CheckpointCursor(values.ToArray());
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

                var candidate = new BsonDocument();
                foreach (BsonElement element in filter)
                {
                    if (element.Name is "$and" or "$or")
                    {
                        continue;
                    }

                    if (element.Value is not BsonDocument)
                    {
                        candidate[element.Name] = element.Value;
                    }
                }

                // Mirror real MongoDB: an upsert that would insert at an already-used _id fails with a
                // duplicate-key error rather than silently creating a second document at the same identity.
                if (candidate.TryGetValue("_id", out BsonValue candidateId) &&
                    State.Documents.Any(item =>
                        item.TryGetValue("_id", out BsonValue existingId) && existingId == candidateId))
                {
                    throw DuplicateKeyException();
                }

                document = candidate;
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

    /// <summary>A non-duplicate-key, non-transaction-capability driver failure, for exception-wrapping tests.</summary>
    internal static MongoCommandException GenericServerErrorException()
    {
        var connectionId = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        return new MongoCommandException(
            connectionId,
            "find",
            new BsonDocument(),
            new BsonDocument
            {
                { "ok", 0 },
                { "code", 50 },
                { "errmsg", "generic server failure for tests" },
            });
    }

    /// <summary>
    /// The exact server rejection reported when transactions are attempted against a standalone deployment
    /// (server error code 20, IllegalOperation), used by <see cref="IsTransactionsUnsupported"/> regression
    /// tests.
    /// </summary>
    internal static MongoCommandException TransactionsUnsupportedException()
    {
        var connectionId = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        return new MongoCommandException(
            connectionId,
            "commitTransaction",
            new BsonDocument(),
            new BsonDocument
            {
                { "ok", 0 },
                { "code", 20 },
                { "errmsg", "Transaction numbers are only allowed on a replica set member or mongos" },
            });
    }

    private Task<DeleteResult> DeleteOneAsync(object?[] args)
    {
        BsonDocument filter = Render((FilterDefinition<BsonDocument>)args[0]!);
        if (State.DeleteException is not null)
        {
            throw State.DeleteException;
        }

        return Task.FromResult<DeleteResult>(State.Locked(() =>
        {
            int index = State.Documents.FindIndex(document => Matches(document, filter));
            if (index >= 0)
            {
                State.Documents.RemoveAt(index);
            }

            return new CheckpointDeleteResult(index >= 0 ? 1 : 0);
        }));
    }

    private static BsonDocument Render(FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry));

    private static bool Matches(BsonDocument document, BsonDocument filter)
    {
        foreach (BsonElement element in filter)
        {
            if (element.Name == "$and")
            {
                if (element.Value.AsBsonArray.Any(sub => !Matches(document, sub.AsBsonDocument)))
                {
                    return false;
                }

                continue;
            }

            if (element.Name == "$or")
            {
                if (!element.Value.AsBsonArray.Any(sub => Matches(document, sub.AsBsonDocument)))
                {
                    return false;
                }

                continue;
            }

            BsonValue actual = document.TryGetValue(element.Name, out BsonValue value) ? value : BsonNull.Value;
            if (element.Value is BsonDocument operation)
            {
                if (operation.TryGetValue("$gt", out BsonValue gt) && actual.CompareTo(gt) <= 0)
                {
                    return false;
                }

                if (operation.TryGetValue("$lte", out BsonValue lte) && actual.CompareTo(lte) > 0)
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

internal class CheckpointIndexManagerProxy : DispatchProxy
{
    public CheckpointCollectionState State { get; set; } = null!;

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
            return Task.FromResult<IAsyncCursor<BsonDocument>>(new CheckpointCursor(indexes));
        }

        throw new NotSupportedException($"Unexpected index call: {targetMethod}");
    }
}

internal sealed class CheckpointCursor(IReadOnlyList<BsonDocument> values) : IAsyncCursor<BsonDocument>
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

internal sealed class CheckpointDeleteResult(long count) : DeleteResult
{
    public override bool IsAcknowledged => true;

    public override long DeletedCount => count;
}
