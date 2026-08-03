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
    public static async Task CreateAsync(
        IMongoSearchIndexManager manager,
        CreateSearchIndexModel model,
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
            // A concurrent Ensure call's create already reached the desired end state.
        }
        catch (MongoException exception)
        {
            throw mapException(exception);
        }
    }

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
