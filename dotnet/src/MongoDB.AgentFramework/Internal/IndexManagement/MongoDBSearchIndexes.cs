using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Search;

namespace MongoDB.AgentFramework.Internal.IndexManagement;

/// <summary>
/// Shared low-level <see cref="IMongoSearchIndexManager"/> mechanics -- find, list, idempotent create, idempotent
/// drop, update, and status classification -- used by both the existing <see cref="MongoDBMemoryProvider"/>/
/// <see cref="MongoDBRAGProvider"/> validate/ensure methods and the new <see cref="MongoDBMemoryIndexManager"/>/
/// <see cref="MongoDBRAGIndexManager"/> facades, so this driver-calling code exists exactly once. Every method
/// that can fail accepts a <c>mapException</c> delegate so each caller preserves its own established exception
/// type for a given failure (for example Memory wraps index-inspection failures as
/// <see cref="MongoDBRetrievalException"/>, while RAG wraps the same failure as <see cref="MongoDBCapabilityException"/>
/// because an unsupported <c>$listSearchIndexes</c> is itself a deployment capability gap for RAG).
/// </summary>
internal static class MongoDBSearchIndexes
{
    /// <summary>Finds a single named index, or <see langword="null"/> if it does not exist.</summary>
    public static async Task<BsonDocument?> FindAsync(
        IMongoSearchIndexManager manager,
        string indexName,
        Func<MongoException, Exception> mapException,
        CancellationToken cancellationToken)
    {
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await manager
                .ListAsync(indexName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                BsonDocument? match = cursor.Current.FirstOrDefault(
                    index => index.GetValue("name", "").AsString == indexName);
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw mapException(exception);
        }
    }

    /// <summary>Lists every Search/Vector Search index on the collection, never mutating MongoDB.</summary>
    public static async Task<IReadOnlyList<BsonDocument>> ListAllAsync(
        IMongoSearchIndexManager manager,
        Func<MongoException, Exception> mapException,
        CancellationToken cancellationToken)
    {
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await manager
                .ListAsync(name: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var results = new List<BsonDocument>();
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                results.AddRange(cursor.Current);
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw mapException(exception);
        }
    }

    /// <summary>
    /// Creates <paramref name="model"/>, treating a concurrent creator having already created the identically
    /// named index as a successful no-op (idempotent Ensure) rather than surfacing an "already exists" failure --
    /// the desired end state was already achieved.
    /// </summary>
    public static Task CreateAsync(
        IMongoSearchIndexManager manager,
        CreateSearchIndexModel model,
        Func<MongoException, Exception> mapException,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(manager, model, static _ => null, mapException, cancellationToken);

    /// <summary>
    /// Shared create mechanics for both the idempotent <see cref="CreateAsync"/> (used by <see cref="EnsureAsync"/>)
    /// and the non-idempotent <see cref="CreateOnlyAsync"/>: both issue the same driver call and both map every
    /// other failure identically, differing only in how an "already exists" race is handled --
    /// <paramref name="onAlreadyExists"/> returning <see langword="null"/> swallows it as a successful no-op
    /// (<see cref="CreateAsync"/>'s contract); returning a non-null exception surfaces it instead
    /// (<see cref="CreateOnlyAsync"/>'s contract, since a caller that explicitly asked to create-only must be told
    /// something was already there rather than silently proceeding).
    /// </summary>
    private static async Task CreateCoreAsync(
        IMongoSearchIndexManager manager,
        CreateSearchIndexModel model,
        Func<MongoException, Exception?> onAlreadyExists,
        Func<MongoException, Exception> mapException,
        CancellationToken cancellationToken)
    {
        try
        {
            await manager.CreateOneAsync(model, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception) when (IsAlreadyExists(exception))
        {
            if (onAlreadyExists(exception) is { } mapped)
            {
                throw mapped;
            }
        }
        catch (MongoException exception)
        {
            throw mapException(exception);
        }
    }

    /// <summary>
    /// The explicit, non-idempotent create-only operation (docs/spec/features/index-management.md lists
    /// <c>create index</c> as distinct from <c>ensure expected definition</c>): fails immediately via
    /// <paramref name="alreadyExistsException"/> if the index already exists (checked both before attempting the
    /// driver call, and -- since a concurrent creator could win the race in between -- again if the driver call
    /// itself reports "already exists"), and otherwise creates it. After any successful create, this always
    /// re-inspects the index and calls <paramref name="validateFinal"/> on its final state before returning it, so
    /// the newly created index is proven to actually match the expected definition rather than merely having
    /// been accepted by the server.
    /// </summary>
    public static async Task<BsonDocument> CreateOnlyAsync(
        IMongoSearchIndexManager manager,
        string indexName,
        SearchIndexType type,
        BsonDocument definitionDocument,
        Action<BsonDocument> validateFinal,
        Func<Exception?, Exception> alreadyExistsException,
        Func<MongoException, Exception> mapCreateException,
        Func<MongoException, Exception> mapInspectionException,
        CancellationToken cancellationToken)
    {
        BsonDocument? existing = await FindAsync(manager, indexName, mapInspectionException, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw alreadyExistsException(null);
        }

        await CreateCoreAsync(
            manager,
            new CreateSearchIndexModel(indexName, type, definitionDocument),
            onAlreadyExists: raceException => alreadyExistsException(raceException),
            mapCreateException,
            cancellationToken).ConfigureAwait(false);

        BsonDocument finalIndex = await RequireReinspectedAsync(
            manager, indexName, mapInspectionException, cancellationToken).ConfigureAwait(false);
        validateFinal(finalIndex);
        return finalIndex;
    }

    /// <summary>
    /// The explicit reconciliation operation (docs/spec/features/index-management.md's <c>ensure expected
    /// definition</c>): creates the index if missing, or updates it if <paramref name="isCompatible"/> reports it
    /// does not match -- but never for a status this does not special-case (for example a terminal
    /// <c>Failed</c> build never triggers an automatic repair attempt here; the state machine requires that to be
    /// explicit, see <see cref="MongoDBIndexFailedException"/>). After any create/update attempt -- including a
    /// create that raced a concurrent caller to an "already exists" no-op -- this always re-inspects the index
    /// and calls <paramref name="validateFinal"/> on its final state before returning it, regardless of whether
    /// the caller will additionally poll for readiness, so a rival concurrent caller having created an
    /// incompatible definition is still caught rather than silently accepted.
    /// </summary>
    public static async Task<BsonDocument> EnsureAsync(
        IMongoSearchIndexManager manager,
        string indexName,
        SearchIndexType type,
        BsonDocument definitionDocument,
        Func<BsonDocument, bool> isCompatible,
        Action<BsonDocument> validateFinal,
        Func<MongoException, Exception> mapCreateException,
        Func<MongoException, Exception> mapUpdateException,
        Func<MongoException, Exception> mapInspectionException,
        CancellationToken cancellationToken)
    {
        BsonDocument? index = await FindAsync(manager, indexName, mapInspectionException, cancellationToken)
            .ConfigureAwait(false);
        if (index is null)
        {
            await CreateAsync(
                manager,
                new CreateSearchIndexModel(indexName, type, definitionDocument),
                mapCreateException,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!isCompatible(index))
        {
            await UpdateAsync(manager, indexName, definitionDocument, mapUpdateException, cancellationToken)
                .ConfigureAwait(false);
        }

        BsonDocument finalIndex = await RequireReinspectedAsync(
            manager, indexName, mapInspectionException, cancellationToken).ConfigureAwait(false);
        validateFinal(finalIndex);
        return finalIndex;
    }

    /// <summary>Re-finds an index that a create/update attempt was just made against, failing actionably if it vanished.</summary>
    private static async Task<BsonDocument> RequireReinspectedAsync(
        IMongoSearchIndexManager manager,
        string indexName,
        Func<MongoException, Exception> mapInspectionException,
        CancellationToken cancellationToken) =>
        await FindAsync(manager, indexName, mapInspectionException, cancellationToken).ConfigureAwait(false) ??
        throw new MongoDBIndexMissingException(
            $"Index '{indexName}' was created or updated but could not be re-inspected afterward.");

    /// <summary>
    /// Drops <paramref name="indexName"/>, treating the index already being absent (for example a concurrent drop,
    /// or the index never having existed) as a successful no-op rather than surfacing a "not found" failure.
    /// </summary>
    public static async Task DropAsync(
        IMongoSearchIndexManager manager,
        string indexName,
        Func<MongoException, Exception> mapException,
        CancellationToken cancellationToken)
    {
        try
        {
            await manager.DropOneAsync(indexName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception) when (IsNotFound(exception))
        {
            // Already absent; dropping a missing index is a successful no-op.
        }
        catch (MongoException exception)
        {
            throw mapException(exception);
        }
    }

    /// <summary>Replaces an existing index's definition. Not idempotent-tolerant: a missing index is an error.</summary>
    public static async Task UpdateAsync(
        IMongoSearchIndexManager manager,
        string indexName,
        BsonDocument definition,
        Func<MongoException, Exception> mapException,
        CancellationToken cancellationToken)
    {
        try
        {
            await manager.UpdateAsync(indexName, definition, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw mapException(exception);
        }
    }

    /// <summary>
    /// Classifies an inspected index document's lifecycle status. A <see langword="null"/> <paramref name="index"/>
    /// (not found) classifies as <see cref="MongoDBIndexStatus.Missing"/>.
    /// </summary>
    public static MongoDBIndexStatus Classify(BsonDocument? index)
    {
        if (index is null)
        {
            return MongoDBIndexStatus.Missing;
        }

        string status = index.GetValue("status", "").AsString;
        if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return MongoDBIndexStatus.Failed;
        }

        if (!string.Equals(status, "READY", StringComparison.OrdinalIgnoreCase))
        {
            return MongoDBIndexStatus.Building;
        }

        return index.GetValue("queryable", false).ToBoolean()
            ? MongoDBIndexStatus.Ready
            : MongoDBIndexStatus.ReadyNotQueryable;
    }

    /// <summary>Gets an inspected index's definition document (<c>latestDefinition</c>, falling back to <c>definition</c>).</summary>
    public static BsonDocument GetDefinition(BsonDocument index) =>
        index.GetValue("latestDefinition", index.GetValue("definition", new BsonDocument())).AsBsonDocument;

    /// <summary>
    /// Detects a server command failure indicating the index already exists (server error code 68/"IndexAlreadyExists",
    /// or an equivalent error message), used to make index creation idempotent under concurrent callers.
    /// </summary>
    public static bool IsAlreadyExists(Exception exception) =>
        exception is MongoCommandException command &&
        (command.Code == 68 ||
         (command.CodeName is { } codeName && codeName.Contains("AlreadyExists", StringComparison.OrdinalIgnoreCase)) ||
         (command.ErrorMessage is { } message && message.Contains("already exists", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Detects a server command failure indicating the index does not exist, used to make index dropping
    /// idempotent regardless of whether it was ever created.
    /// </summary>
    public static bool IsNotFound(Exception exception) =>
        exception is MongoCommandException command &&
        ((command.CodeName is { } codeName && codeName.Contains("NotFound", StringComparison.OrdinalIgnoreCase)) ||
         (command.ErrorMessage is { } message &&
          (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// Detects a server command failure indicating the connected identity lacks the privileges required for the
    /// attempted index operation (server error code 13/"Unauthorized", or an equivalent error message), surfaced
    /// distinctly via <see cref="MongoDBIndexPrivilegeException"/> rather than a generic deployment error.
    /// </summary>
    public static bool IsUnauthorized(Exception exception) =>
        exception is MongoCommandException command &&
        (command.Code == 13 ||
         string.Equals(command.CodeName, "Unauthorized", StringComparison.OrdinalIgnoreCase) ||
         (command.ErrorMessage is { } message && message.Contains("not authorized", StringComparison.OrdinalIgnoreCase)));
}
