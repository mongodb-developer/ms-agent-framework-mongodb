using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using MongoDB.AgentFramework.Internal;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MongoDB.AgentFramework;

/// <summary>
/// Persists immutable, versioned, authorized workflow checkpoints -- resumable execution state, pending
/// requests, executor state, and checkpoint lineage -- through the public
/// <see cref="Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.Agents.AI.Workflows</c> (verified at the pinned floor 1.13.0, with the
/// <see cref="Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore"/> abstract contract itself
/// unchanged through the latest published 1.16.0; see
/// docs/development/persistence/dotnet-checkpoint-contract-research.md) publishes exactly one public
/// checkpoint-storage extension point: the abstract
/// <see cref="Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore"/> class, whose three abstract
/// hooks (<see cref="CreateCheckpointAsync"/>, <see cref="RetrieveCheckpointAsync"/>,
/// <see cref="RetrieveIndexAsync"/>) accept no <see cref="CancellationToken"/> parameter and give
/// <see cref="CreateCheckpointAsync"/> no way for a caller to supply an explicit checkpoint identifier -- the
/// store always allocates a fresh one. This is a real, verified framework design constraint (confirmed against
/// the reference <c>FileSystemJsonCheckpointStore</c> and <c>CosmosCheckpointStore</c> implementations shipped in
/// the same repository), not a design choice this type can avoid. <see cref="MongoDBCheckpointStore"/> therefore
/// exposes a richer, cancellable, explicitly-identified public facade
/// (<see cref="SaveCheckpointAsync"/>/<see cref="LoadCheckpointAsync"/>/<see cref="ListCheckpointsAsync"/>/
/// <see cref="GetLatestCheckpointAsync"/>/<see cref="DeleteCheckpointAsync"/>) alongside the three required
/// framework hooks, which delegate to the same internal storage core so both surfaces observe identical
/// idempotency, lineage, and version-gate behavior.
/// </para>
/// <para>
/// <c>Microsoft.Agents.AI.Workflows.CheckpointManager</c> (a separate, non-abstract public type layered over
/// <see cref="Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore"/>, not part of the extension
/// contract itself) is <em>not</em> identical across the verified range: its convenience
/// <c>GetLatestCheckpointAsync(string, CancellationToken)</c> method exists at 1.16.0 but not at the pinned
/// floor 1.13.0 (verified by reflection; see
/// docs/development/persistence/dotnet-checkpoint-contract-research.md). Nothing in this type depends on that
/// method; <see cref="RetrieveIndexAsync"/> always returns checkpoints in ascending, monotonic <c>sequence</c>
/// order specifically so that any caller -- including one restricted to the 1.13.0 floor and using only
/// <see cref="RetrieveIndexAsync"/> directly -- can find the latest checkpoint as the index's last element.
/// </para>
/// <para>
/// This build only supports the resolved <c>Microsoft.Agents.AI.Workflows</c> assembly versions in
/// <c>[<see cref="MinimumSupportedFrameworkAssemblyVersion"/>, <see cref="MaximumSupportedFrameworkAssemblyVersionExclusive"/>)</c>;
/// every constructor validates the resolved assembly version and throws <see cref="MongoDBConfigurationException"/>
/// for any other version. Stored documents also carry an explicit <c>schema_version</c> marker; a document
/// written by an unsupported schema version is never read, updated, or deleted -- see
/// docs/development/persistence/dotnet-checkpoint-store-migration.md for the required manual remediation.
/// </para>
/// </remarks>
public sealed class MongoDBCheckpointStore : JsonCheckpointStore, IAsyncDisposable
{
    /// <summary>The stored MongoDB envelope schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The minimum resolved <c>Microsoft.Agents.AI.Workflows</c> assembly version this build has verified
    /// (inclusive). See docs/development/persistence/dotnet-checkpoint-contract-research.md.
    /// </summary>
    internal static readonly Version MinimumSupportedFrameworkAssemblyVersion = new(1, 13, 0, 0);

    /// <summary>
    /// The upper bound (exclusive) of the resolved <c>Microsoft.Agents.AI.Workflows</c> assembly version this
    /// build has verified. See docs/development/persistence/dotnet-checkpoint-contract-research.md.
    /// </summary>
    internal static readonly Version MaximumSupportedFrameworkAssemblyVersionExclusive = new(1, 17, 0, 0);

    private const string CheckpointDocType = "checkpoint";
    private const string SequenceCounterDocType = "sequence_counter";
    private const string ContinuationTokenVersion = "v1";

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly MongoDBCheckpointStoreOptions _options;
    private readonly OwnedResource<IMongoClient>? _client;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a store over an injected collection, which remains caller-owned.</summary>
    public MongoDBCheckpointStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBCheckpointStoreOptions options)
        : this(collection, options, DefaultResolvedFrameworkAssemblyVersionProvider, DefaultClock)
    {
    }

    /// <summary>
    /// Test-only seam allowing the resolved framework assembly version to be injected instead of inspected from
    /// the loaded <see cref="JsonCheckpointStore"/> assembly, so unsupported-version rejection is unit-testable
    /// without loading multiple real assembly versions side by side.
    /// </summary>
    internal MongoDBCheckpointStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBCheckpointStoreOptions options,
        Func<Version> resolvedFrameworkAssemblyVersionProvider)
        : this(collection, options, resolvedFrameworkAssemblyVersionProvider, DefaultClock)
    {
    }

    /// <summary>Test-only seam allowing "now" to be injected instead of <see cref="DateTimeOffset.UtcNow"/>.</summary>
    internal MongoDBCheckpointStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBCheckpointStoreOptions options,
        Func<DateTimeOffset> clock)
        : this(collection, options, DefaultResolvedFrameworkAssemblyVersionProvider, clock)
    {
    }

    /// <summary>Test-only seam allowing both the resolved framework assembly version and "now" to be injected.</summary>
    internal MongoDBCheckpointStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBCheckpointStoreOptions options,
        Func<Version> resolvedFrameworkAssemblyVersionProvider,
        Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolvedFrameworkAssemblyVersionProvider);
        ArgumentNullException.ThrowIfNull(clock);
        options.Validate();
        ValidateResolvedFrameworkAssemblyVersion(resolvedFrameworkAssemblyVersionProvider());
        _options = options with
        {
            TenantId = options.TenantId?.Trim(),
            WorkflowId = options.WorkflowId.Trim(),
        };
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _clock = clock;
    }

    /// <summary>Creates a store over an injected database, which remains caller-owned.</summary>
    public MongoDBCheckpointStore(
        IMongoDatabase database,
        string collectionName,
        MongoDBCheckpointStoreOptions options)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database))).GetCollection<BsonDocument>(
                MongoDBCheckpointStoreOptions.RequireText(collectionName, nameof(collectionName))),
            options)
    {
    }

    /// <summary>Creates a store over an injected client, which remains caller-owned.</summary>
    public MongoDBCheckpointStore(
        IMongoClient client,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client))).GetDatabase(
                MongoDBCheckpointStoreOptions.RequireText(databaseName, nameof(databaseName))),
            collectionName,
            options)
    {
    }

    /// <summary>Creates a provider-owned client from a connection string.</summary>
    public MongoDBCheckpointStore(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options)
        : this(connectionString, databaseName, collectionName, options, clientFactory: null)
    {
    }

    /// <summary>
    /// Test-only seam mirroring <see cref="MongoClientFactory.FromConnectionString"/>'s existing
    /// <c>clientFactory</c> override, proving a construction failure occurring after the owned client is
    /// created still disposes it.
    /// </summary>
    internal MongoDBCheckpointStore(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options,
        Func<string, IMongoClient>? clientFactory)
        : this(connectionString, databaseName, collectionName, options, clientFactory,
              DefaultResolvedFrameworkAssemblyVersionProvider)
    {
    }

    /// <summary>Test-only seam additionally allowing the resolved framework assembly version to be injected.</summary>
    internal MongoDBCheckpointStore(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options,
        Func<string, IMongoClient>? clientFactory,
        Func<Version> resolvedFrameworkAssemblyVersionProvider)
        : this(Connect(
            connectionString, databaseName, collectionName, options, clientFactory,
            resolvedFrameworkAssemblyVersionProvider))
    {
    }

    private MongoDBCheckpointStore(
        (OwnedResource<IMongoClient> Client,
         IMongoCollection<BsonDocument> Collection,
         MongoDBCheckpointStoreOptions Options,
         Func<Version> VersionProvider) connected)
        : this(connected.Collection, connected.Options, connected.VersionProvider)
    {
        _client = connected.Client;
    }

    /// <summary>
    /// Validates every constructor argument that does not require a MongoDB client entirely before creating an
    /// owned client, and disposes the client if a later database/collection-resolution step fails. Mirrors
    /// <see cref="MongoDBAgentSessionStore"/>'s equivalent construction-exception-safety design.
    /// </summary>
    private static (OwnedResource<IMongoClient> Client,
        IMongoCollection<BsonDocument> Collection,
        MongoDBCheckpointStoreOptions Options,
        Func<Version> VersionProvider) Connect(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options,
        Func<string, IMongoClient>? clientFactory,
        Func<Version> resolvedFrameworkAssemblyVersionProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolvedFrameworkAssemblyVersionProvider);
        options.Validate();
        ValidateResolvedFrameworkAssemblyVersion(resolvedFrameworkAssemblyVersionProvider());
        string validDatabaseName = MongoDBCheckpointStoreOptions.RequireText(databaseName, nameof(databaseName));
        string validCollectionName =
            MongoDBCheckpointStoreOptions.RequireText(collectionName, nameof(collectionName));

        OwnedResource<IMongoClient> client = MongoClientFactory.FromConnectionString(connectionString, clientFactory);
        try
        {
            IMongoCollection<BsonDocument> collection = client.Value
                .GetDatabase(validDatabaseName)
                .GetCollection<BsonDocument>(validCollectionName);
            return (client, collection, options, resolvedFrameworkAssemblyVersionProvider);
        }
        catch
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Gets whether this store owns its MongoDB client.</summary>
    public bool OwnsClient => _client?.OwnsValue is true;

    private static DateTimeOffset DefaultClock() => DateTimeOffset.UtcNow;

    private static Version DefaultResolvedFrameworkAssemblyVersionProvider() =>
        typeof(JsonCheckpointStore).Assembly.GetName().Version
            ?? throw new MongoDBConfigurationException(
                "Unable to determine the resolved Microsoft.Agents.AI.Workflows assembly version.");

    private static void ValidateResolvedFrameworkAssemblyVersion(Version resolvedVersion)
    {
        if (resolvedVersion < MinimumSupportedFrameworkAssemblyVersion ||
            resolvedVersion >= MaximumSupportedFrameworkAssemblyVersionExclusive)
        {
            throw new MongoDBConfigurationException(
                $"MongoDBCheckpointStore has verified Microsoft.Agents.AI.Workflows " +
                $"[{MinimumSupportedFrameworkAssemblyVersion},{MaximumSupportedFrameworkAssemblyVersionExclusive}) " +
                $"only (see docs/development/persistence/dotnet-checkpoint-contract-research.md), but the " +
                $"resolved assembly reports version {resolvedVersion}. Pin a verified " +
                "Microsoft.Agents.AI.Workflows version, or re-run the compatibility verification in that " +
                "document and widen this range, before using this version.");
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Required JsonCheckpointStore hooks (framework-facing; no CancellationToken parameter is available --
    // see class remarks).
    // ---------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// Always allocates a fresh <see cref="CheckpointInfo.CheckpointId"/> (the base contract gives callers no
    /// way to request one), applies this store's configured <see cref="MongoDBCheckpointStoreOptions.DefaultExpiration"/>
    /// if any (the base contract has no expiry parameter), and runs with no external cancellation.
    /// </remarks>
    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId,
        JsonElement value,
        CheckpointInfo? parent = null)
    {
        string checkpointId = Guid.NewGuid().ToString("N");
        MongoDBCheckpointRecord record = await SaveCheckpointCoreAsync(
            sessionId,
            checkpointId,
            value,
            parent?.CheckpointId,
            expiresAt: null,
            CancellationToken.None).ConfigureAwait(false);
        return new CheckpointInfo(record.SessionId, record.CheckpointId);
    }

    /// <inheritdoc/>
    /// <exception cref="KeyNotFoundException">No checkpoint with <paramref name="key"/> exists in scope.</exception>
    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);
        MongoDBCheckpointRecord? record = await LoadCheckpointAsync(sessionId, key.CheckpointId, CancellationToken.None)
            .ConfigureAwait(false);
        return record is null
            ? throw new KeyNotFoundException(
                $"Checkpoint '{key.CheckpointId}' not found for session '{sessionId}'.")
            : record.Payload;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns every matching checkpoint (the base contract is unbounded), in ascending, monotonic
    /// <c>sequence</c> order -- never timestamp order -- so framework callers such as
    /// <c>CheckpointManager.GetLatestCheckpointAsync</c> that rely on this ordering to find the head checkpoint
    /// observe correct results. Internally paged in bounded batches to avoid one unbounded query.
    /// </remarks>
    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId,
        CheckpointInfo? withParent = null)
    {
        var results = new List<CheckpointInfo>();
        string? continuationToken = null;
        do
        {
            MongoDBCheckpointPage page = await ListCheckpointsAsync(
                sessionId,
                limit: 1_000,
                continuationToken,
                CancellationToken.None).ConfigureAwait(false);
            results.AddRange(
                page.Items
                    .Where(item => withParent is null || item.ParentCheckpointId == withParent.CheckpointId)
                    .Select(item => new CheckpointInfo(item.SessionId, item.CheckpointId)));
            continuationToken = page.ContinuationToken;
        } while (continuationToken is not null);

        return results;
    }

    // ---------------------------------------------------------------------------------------------------
    // Explicit, cancellable, richer public facade.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Saves a checkpoint under an explicit, caller-supplied identifier. Idempotent when a checkpoint with the
    /// same <paramref name="checkpointId"/> already exists in scope with identical payload bytes and parent
    /// lineage (a converging retry); throws <see cref="MongoDBConcurrencyException"/> if it exists with a
    /// different payload or parent, since checkpoints are immutable historical records.
    /// </summary>
    public async Task<MongoDBCheckpointRecord> SaveCheckpointAsync(
        string sessionId,
        string checkpointId,
        JsonElement payload,
        string? parentCheckpointId = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            token => SaveCheckpointCoreAsync(sessionId, checkpointId, payload, parentCheckpointId, expiresAt, token),
            _options.PersistenceTimeout,
            "MongoDB Workflow Checkpoint Store persistence deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads a checkpoint by its explicit identifier, or <see langword="null"/> if absent.</summary>
    public async Task<MongoDBCheckpointRecord?> LoadCheckpointAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        BsonDocument scope = Scope(sessionId);
        MongoDBCheckpointStoreOptions.RequireText(checkpointId, nameof(checkpointId));
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                try
                {
                    BsonDocument? document = await FindOneAsync(IdentityFilter(scope, sessionId, checkpointId), token)
                        .ConfigureAwait(false);
                    return document is null ? null : ToRecord(document);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (MongoDBIntegrationException)
                {
                    throw;
                }
                catch (MongoException exception)
                {
                    throw new MongoDBRetrievalException(
                        "MongoDB Workflow Checkpoint Store retrieval failed.",
                        exception);
                }
            },
            _options.RetrievalTimeout,
            "MongoDB Workflow Checkpoint Store retrieval deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the checkpoint with the greatest monotonic <c>sequence</c> for <paramref name="sessionId"/>, or
    /// <see langword="null"/> if none exist. Never orders by timestamp.
    /// </summary>
    public async Task<MongoDBCheckpointRecord?> GetLatestCheckpointAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        BsonDocument scope = Scope(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                try
                {
                    var findOptions = new FindOptions<BsonDocument, BsonDocument>
                    {
                        Sort = Builders<BsonDocument>.Sort.Descending("sequence"),
                        Limit = 1,
                    };
                    using IAsyncCursor<BsonDocument> cursor = await _collection.FindAsync(
                        ScopeSessionFilter(scope, sessionId),
                        findOptions,
                        token).ConfigureAwait(false);
                    BsonDocument? document = await cursor.MoveNextAsync(token).ConfigureAwait(false)
                        ? cursor.Current.FirstOrDefault()
                        : null;
                    return document is null ? null : ToRecord(document);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (MongoDBIntegrationException)
                {
                    throw;
                }
                catch (MongoException exception)
                {
                    throw new MongoDBRetrievalException(
                        "MongoDB Workflow Checkpoint Store retrieval failed.",
                        exception);
                }
            },
            _options.RetrievalTimeout,
            "MongoDB Workflow Checkpoint Store retrieval deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists checkpoint summaries (no payload) in ascending <c>sequence</c> order, bounded to
    /// <paramref name="limit"/> items per call, with an opaque scoped/versioned/tamper-rejecting continuation
    /// token for the next page.
    /// </summary>
    public async Task<MongoDBCheckpointPage> ListCheckpointsAsync(
        string sessionId,
        int limit,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10_000)
        {
            throw new MongoDBConfigurationException("limit must be between 1 and 10000.");
        }

        BsonDocument scope = Scope(sessionId);
        long? afterSequence = continuationToken is null
            ? null
            : DecodeContinuationToken(scope, sessionId, continuationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                try
                {
                    FilterDefinition<BsonDocument> filter = ScopeSessionFilter(scope, sessionId);
                    if (afterSequence is { } after)
                    {
                        filter &= Builders<BsonDocument>.Filter.Gt("sequence", after);
                    }

                    var findOptions = new FindOptions<BsonDocument, BsonDocument>
                    {
                        Sort = Builders<BsonDocument>.Sort.Ascending("sequence"),
                        Limit = limit + 1,
                    };
                    using IAsyncCursor<BsonDocument> cursor = await _collection.FindAsync(
                        filter,
                        findOptions,
                        token).ConfigureAwait(false);
                    var documents = new List<BsonDocument>();
                    while (await cursor.MoveNextAsync(token).ConfigureAwait(false))
                    {
                        documents.AddRange(cursor.Current);
                    }

                    bool hasMore = documents.Count > limit;
                    if (hasMore)
                    {
                        documents.RemoveAt(documents.Count - 1);
                    }

                    return new MongoDBCheckpointPage
                    {
                        Items = documents.Select(ToSummary).ToArray(),
                        ContinuationToken = hasMore
                            ? EncodeContinuationToken(scope, sessionId, documents[^1]["sequence"].ToInt64())
                            : null,
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (MongoDBIntegrationException)
                {
                    throw;
                }
                catch (MongoException exception)
                {
                    throw new MongoDBRetrievalException(
                        "MongoDB Workflow Checkpoint Store list failed.",
                        exception);
                }
            },
            _options.RetrievalTimeout,
            "MongoDB Workflow Checkpoint Store retrieval deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a checkpoint by its explicit identifier. Returns <see langword="false"/> when no matching
    /// checkpoint exists (an idempotent no-op). Deleting a checkpoint that is another checkpoint's lineage
    /// parent leaves a lineage gap; this is documented, not prevented.
    /// </summary>
    public async Task<bool> DeleteCheckpointAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default)
    {
        BsonDocument scope = Scope(sessionId);
        MongoDBCheckpointStoreOptions.RequireText(checkpointId, nameof(checkpointId));
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                FilterDefinition<BsonDocument> filter = IdentityFilter(scope, sessionId, checkpointId) &
                    Builders<BsonDocument>.Filter.Eq("schema_version", SchemaVersion);
                DeleteResult result = await _collection.DeleteOneAsync(filter, token).ConfigureAwait(false);
                if (!result.IsAcknowledged)
                {
                    throw new MongoDBPersistenceException(
                        "MongoDB Workflow Checkpoint Store delete was not acknowledged.");
                }

                if (result.DeletedCount > 0)
                {
                    return true;
                }

                BsonDocument? existing = await FindOneAsync(IdentityFilter(scope, sessionId, checkpointId), token)
                    .ConfigureAwait(false);
                if (existing is not null && !HasCompatibleSchema(existing))
                {
                    throw IncompatibleSchemaException();
                }

                return false;
            },
            _options.PersistenceTimeout,
            "MongoDB Workflow Checkpoint Store persistence deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicitly provisions the required regular lookup indexes and the optional TTL index. Never called
    /// implicitly during construction, saves, or retrieval.
    /// </summary>
    public async Task<IReadOnlyList<string>> EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkpointOnly = new BsonDocument("doc_type", CheckpointDocType);
        var models = new List<CreateIndexModel<BsonDocument>>
        {
            new(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("tenant_id")
                    .Ascending("workflow_id")
                    .Ascending("session_id")
                    .Ascending("checkpoint_id"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "checkpoint_identity_lookup",
                    Unique = true,
                    PartialFilterExpression = checkpointOnly,
                }),
            new(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("tenant_id")
                    .Ascending("workflow_id")
                    .Ascending("session_id")
                    .Ascending("sequence"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "checkpoint_sequence_lookup",
                    PartialFilterExpression = checkpointOnly,
                }),
            new(
                Builders<BsonDocument>.IndexKeys.Ascending("expires_at"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "checkpoint_expiration_ttl",
                    ExpireAfter = TimeSpan.Zero,
                    PartialFilterExpression = new BsonDocument(
                        "expires_at",
                        new BsonDocument("$type", "date")),
                }),
        };
        try
        {
            return (await _collection.Indexes.CreateManyAsync(models, cancellationToken)
                .ConfigureAwait(false)).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw new MongoDBPersistenceException(
                "MongoDB Workflow Checkpoint Store index provisioning failed.",
                exception);
        }
    }

    /// <summary>Validates the required regular and TTL indexes without mutating MongoDB.</summary>
    public async Task ValidateIndexesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await _collection.Indexes.ListAsync(cancellationToken)
                .ConfigureAwait(false);
            var indexes = new List<BsonDocument>();
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                indexes.AddRange(cursor.Current);
            }

            ValidateIndex(
                indexes,
                "checkpoint_identity_lookup",
                ["tenant_id", "workflow_id", "session_id", "checkpoint_id"],
                expectedUnique: true);
            ValidateIndex(
                indexes,
                "checkpoint_sequence_lookup",
                ["tenant_id", "workflow_id", "session_id", "sequence"],
                expectedUnique: false);
            BsonDocument ttl = ValidateIndex(
                indexes,
                "checkpoint_expiration_ttl",
                ["expires_at"],
                expectedUnique: false);
            if (!ttl.TryGetValue("expireAfterSeconds", out BsonValue seconds) ||
                seconds.IsBsonNull ||
                seconds.ToDouble() != 0)
            {
                throw new MongoDBIndexMismatchException(
                    "Regular index 'checkpoint_expiration_ttl' does not match the required Workflow Checkpoint " +
                    "Store definition.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoDBIntegrationException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw new MongoDBRetrievalException(
                "MongoDB Workflow Checkpoint Store index validation failed.",
                exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Shared internal core.
    // ---------------------------------------------------------------------------------------------------

    private async Task<MongoDBCheckpointRecord> SaveCheckpointCoreAsync(
        string sessionId,
        string checkpointId,
        JsonElement payload,
        string? parentCheckpointId,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        BsonDocument scope = Scope(sessionId);
        MongoDBCheckpointStoreOptions.RequireText(checkpointId, nameof(checkpointId));
        BsonBinaryData payloadBytes = SerializePayload(payload);
        DateTimeOffset now = _clock();
        DateTimeOffset? effectiveExpiresAt = expiresAt ?? DefaultExpiresAt(now);

        // Check for an existing checkpoint before allocating a sequence number, so a purely idempotent retry
        // (the common case) never burns a sequence value. A genuine race between two concurrent first writers
        // for the same identifier is still handled safely below via the insert-time duplicate-key path.
        BsonDocument? existing = await FindOneAsync(IdentityFilter(scope, sessionId, checkpointId), cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!HasCompatibleSchema(existing))
            {
                throw IncompatibleSchemaException();
            }

            if (ContentEquals(existing, payloadBytes, parentCheckpointId))
            {
                return ToRecord(existing);
            }

            throw ConflictException(sessionId, checkpointId);
        }

        long sequence = await AllocateSequenceAsync(scope, sessionId, cancellationToken).ConfigureAwait(false);
        BsonDocument candidate = BuildCheckpointDocument(
            scope, sessionId, checkpointId, parentCheckpointId, sequence, payloadBytes, now, effectiveExpiresAt);
        try
        {
            await _collection.InsertOneAsync(candidate, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException exception) when (IsDuplicateKey(exception))
        {
            // Another concurrent caller won the race for this exact checkpoint identifier. The failed insert
            // did not mutate the winner's document; detect and reject/converge read-only.
            BsonDocument? raced = await FindOneAsync(IdentityFilter(scope, sessionId, checkpointId), cancellationToken)
                .ConfigureAwait(false);
            if (raced is not null && !HasCompatibleSchema(raced))
            {
                throw IncompatibleSchemaException();
            }

            if (raced is not null && ContentEquals(raced, payloadBytes, parentCheckpointId))
            {
                return ToRecord(raced);
            }

            throw ConflictException(sessionId, checkpointId, exception);
        }

        return ToRecord(candidate);
    }

    private async Task<long> AllocateSequenceAsync(
        BsonDocument scope,
        string sessionId,
        CancellationToken cancellationToken)
    {
        string counterId = SequenceCounterDocumentId(scope, sessionId);
        FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("_id", counterId);
        UpdateDefinition<BsonDocument> update = Builders<BsonDocument>.Update
            .Inc("sequence_value", 1L)
            .SetOnInsert("doc_type", SequenceCounterDocType)
            .SetOnInsert("tenant_id", scope["tenant_id"])
            .SetOnInsert("workflow_id", scope["workflow_id"])
            .SetOnInsert("session_id", sessionId);
        BsonDocument result = await _collection.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<BsonDocument, BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken).ConfigureAwait(false);
        return result["sequence_value"].ToInt64();
    }

    private static BsonDocument BuildCheckpointDocument(
        BsonDocument scope,
        string sessionId,
        string checkpointId,
        string? parentCheckpointId,
        long sequence,
        BsonBinaryData payloadBytes,
        DateTimeOffset now,
        DateTimeOffset? effectiveExpiresAt) =>
        new()
        {
            { "_id", CheckpointDocumentId(scope, sessionId, checkpointId) },
            { "doc_type", CheckpointDocType },
            { "schema_version", SchemaVersion },
            { "tenant_id", scope["tenant_id"] },
            { "workflow_id", scope["workflow_id"] },
            { "session_id", sessionId },
            { "checkpoint_id", checkpointId },
            { "parent_checkpoint_id", parentCheckpointId is null ? BsonNull.Value : parentCheckpointId },
            { "sequence", sequence },
            { "created_at", now.UtcDateTime },
            {
                "expires_at",
                effectiveExpiresAt is { } expires ? (BsonValue)expires.UtcDateTime : BsonNull.Value
            },
            { "checkpoint", payloadBytes },
        };

    private BsonDocument IsolationScope() =>
        new()
        {
            { "tenant_id", _options.TenantId is null ? BsonNull.Value : _options.TenantId },
            { "workflow_id", _options.WorkflowId },
        };

    private BsonDocument Scope(string sessionId)
    {
        // sessionId is opaque and must not be trimmed, mirroring MongoDBAgentSessionStore's session_id handling.
        MongoDBCheckpointStoreOptions.RequireText(sessionId, nameof(sessionId));
        BsonDocument dimensions = IsolationScope();
        return new BsonDocument
        {
            { "scope_discriminator", CanonicalScopeDiscriminator(_options.TenantId, _options.WorkflowId) },
            { "tenant_id", dimensions["tenant_id"] },
            { "workflow_id", dimensions["workflow_id"] },
        };
    }

    private static FilterDefinition<BsonDocument> ScopeFilter(BsonDocument scope) =>
        Builders<BsonDocument>.Filter.Eq("tenant_id", scope["tenant_id"]) &
        Builders<BsonDocument>.Filter.Eq("workflow_id", scope["workflow_id"]);

    private static FilterDefinition<BsonDocument> ScopeSessionFilter(BsonDocument scope, string sessionId) =>
        ScopeFilter(scope) &
        Builders<BsonDocument>.Filter.Eq("session_id", sessionId) &
        Builders<BsonDocument>.Filter.Eq("doc_type", CheckpointDocType);

    private static FilterDefinition<BsonDocument> IdentityFilter(BsonDocument scope, string sessionId, string checkpointId) =>
        Builders<BsonDocument>.Filter.Eq("_id", CheckpointDocumentId(scope, sessionId, checkpointId)) &
        ScopeFilter(scope) &
        Builders<BsonDocument>.Filter.Eq("session_id", sessionId) &
        Builders<BsonDocument>.Filter.Eq("checkpoint_id", checkpointId) &
        Builders<BsonDocument>.Filter.Eq("doc_type", CheckpointDocType);

    private DateTimeOffset? DefaultExpiresAt(DateTimeOffset now) =>
        _options.DefaultExpiration is { } defaultExpiration ? now + defaultExpiration : null;

    private async Task<BsonDocument?> FindOneAsync(
        FilterDefinition<BsonDocument> filter,
        CancellationToken cancellationToken)
    {
        using IAsyncCursor<BsonDocument> cursor = await _collection.FindAsync(
            filter,
            new FindOptions<BsonDocument, BsonDocument> { Limit = 1 },
            cancellationToken).ConfigureAwait(false);
        return await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false)
            ? cursor.Current.FirstOrDefault()
            : null;
    }

    private static BsonBinaryData SerializePayload(JsonElement payload)
    {
        try
        {
            // Stored as the exact UTF-8 JSON bytes, never re-parsed through BsonDocument, so unknown/future
            // framework payload shapes -- including numeric literals beyond double precision -- round-trip
            // byte-for-byte. Mirrors MongoDBAgentSessionStore's session payload storage convention.
            byte[] bytes = Encoding.UTF8.GetBytes(payload.GetRawText());
            return new BsonBinaryData(bytes, BsonBinarySubType.Binary);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new MongoDBMappingException(
                "Workflow checkpoint payload could not be serialized losslessly.",
                exception);
        }
    }

    private static MongoDBCheckpointRecord ToRecord(BsonDocument document)
    {
        ValidateSchemaVersion(document);
        return new MongoDBCheckpointRecord
        {
            SessionId = document["session_id"].AsString,
            CheckpointId = document["checkpoint_id"].AsString,
            ParentCheckpointId = document.TryGetValue("parent_checkpoint_id", out BsonValue parent) && !parent.IsBsonNull
                ? parent.AsString
                : null,
            Sequence = document["sequence"].ToInt64(),
            Payload = DeserializePayloadElement(document),
            CreatedAt = new DateTimeOffset(document["created_at"].ToUniversalTime()),
            ExpiresAt = document.TryGetValue("expires_at", out BsonValue expires) && !expires.IsBsonNull
                ? new DateTimeOffset(expires.ToUniversalTime())
                : null,
        };
    }

    private static MongoDBCheckpointSummary ToSummary(BsonDocument document)
    {
        ValidateSchemaVersion(document);
        return new MongoDBCheckpointSummary
        {
            SessionId = document["session_id"].AsString,
            CheckpointId = document["checkpoint_id"].AsString,
            ParentCheckpointId = document.TryGetValue("parent_checkpoint_id", out BsonValue parent) && !parent.IsBsonNull
                ? parent.AsString
                : null,
            Sequence = document["sequence"].ToInt64(),
            CreatedAt = new DateTimeOffset(document["created_at"].ToUniversalTime()),
            ExpiresAt = document.TryGetValue("expires_at", out BsonValue expires) && !expires.IsBsonNull
                ? new DateTimeOffset(expires.ToUniversalTime())
                : null,
        };
    }

    private static void ValidateSchemaVersion(BsonDocument document)
    {
        if (!HasCompatibleSchema(document))
        {
            throw IncompatibleSchemaException();
        }
    }

    private static bool HasCompatibleSchema(BsonDocument document) =>
        document.TryGetValue("schema_version", out BsonValue schema) &&
        schema.IsInt32 && schema.AsInt32 == SchemaVersion;

    private static MongoDBMappingException IncompatibleSchemaException() =>
        new(
            "The stored checkpoint at this authorized identity was written with an unsupported schema_version " +
            "for this build (expected schema_version " + SchemaVersion.ToString(CultureInfo.InvariantCulture) +
            "). No read, update, or delete was attempted against it. Follow the manual remediation in " +
            "docs/development/persistence/dotnet-checkpoint-store-migration.md before retrying.");

    private static MongoDBConcurrencyException ConflictException(
        string sessionId, string checkpointId, Exception? innerException = null)
    {
        const string Message =
            "A checkpoint with this identifier already exists in scope with a different payload or parent " +
            "lineage. Checkpoints are immutable historical records; use a new checkpoint id for a new " +
            "checkpoint.";
        return innerException is null
            ? new MongoDBConcurrencyException($"{Message} (session '{sessionId}', checkpoint '{checkpointId}')")
            : new MongoDBConcurrencyException(
                $"{Message} (session '{sessionId}', checkpoint '{checkpointId}')", innerException);
    }

    private static JsonElement DeserializePayloadElement(BsonDocument document)
    {
        if (!document.TryGetValue("checkpoint", out BsonValue payload) || payload.BsonType != BsonType.Binary)
        {
            throw new MongoDBMappingException(
                "Stored Workflow Checkpoint Store payload is invalid. Follow the manual remediation in " +
                "docs/development/persistence/dotnet-checkpoint-store-migration.md before retrying.");
        }

        try
        {
            byte[] bytes = payload.AsBsonBinaryData.Bytes;
            using JsonDocument parsed = JsonDocument.Parse(bytes);
            return parsed.RootElement.Clone();
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new MongoDBMappingException(
                "Stored Workflow Checkpoint Store payload is incompatible. Follow the manual remediation in " +
                "docs/development/persistence/dotnet-checkpoint-store-migration.md before retrying.",
                exception);
        }
    }

    /// <summary>
    /// Compares stored envelope state against a candidate write for idempotent-retry convergence: payload bytes
    /// and parent lineage must match exactly. Unlike <c>MongoDBAgentSessionStore</c>, checkpoints are immutable
    /// historical records with no compare-and-swap update path, so expiry is deliberately excluded from this
    /// comparison -- a converging retry never needs (or is able) to change a previously committed expiry.
    /// </summary>
    private static bool ContentEquals(
        BsonDocument existing,
        BsonBinaryData candidatePayload,
        string? candidateParentCheckpointId) =>
        existing.TryGetValue("checkpoint", out BsonValue existingPayload) &&
        existingPayload.BsonType == BsonType.Binary &&
        existingPayload.AsBsonBinaryData.Bytes.AsSpan().SequenceEqual(candidatePayload.Bytes) &&
        ParentEquals(existing, candidateParentCheckpointId);

    private static bool ParentEquals(BsonDocument existing, string? candidateParentCheckpointId)
    {
        bool existingHasParent =
            existing.TryGetValue("parent_checkpoint_id", out BsonValue parent) && !parent.IsBsonNull;
        if (!existingHasParent)
        {
            return candidateParentCheckpointId is null;
        }

        return candidateParentCheckpointId is not null &&
            string.Equals(parent.AsString, candidateParentCheckpointId, StringComparison.Ordinal);
    }

    private static bool IsDuplicateKey(MongoException exception) =>
        exception is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } ||
        exception is MongoCommandException { Code: 11000 or 11001 };

    private static string CheckpointDocumentId(BsonDocument scope, string sessionId, string checkpointId) =>
        Hash($"checkpoint|{scope["scope_discriminator"].AsString}|{sessionId}|{checkpointId}");

    private static string SequenceCounterDocumentId(BsonDocument scope, string sessionId) =>
        Hash($"sequence_counter|{scope["scope_discriminator"].AsString}|{sessionId}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string CanonicalScopeDiscriminator(string? tenantId, string workflowId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("dimensions");
            writer.WriteStartObject();
            writer.WriteString("workflow_id", workflowId);
            if (tenantId is null)
            {
                writer.WriteNull("tenant_id");
            }
            else
            {
                writer.WriteString("tenant_id", tenantId);
            }

            writer.WriteEndObject();
            writer.WriteNumber("version", 1);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>
    /// Encodes a scoped, versioned, self-verifying continuation token. The signing key is derived from this
    /// store's own scope discriminator, so a token issued by a differently scoped store (different tenant or
    /// workflow) fails signature verification rather than silently returning the wrong scope's data, and any
    /// alteration of the encoded sequence, session, or scope invalidates the signature.
    /// </summary>
    private static string EncodeContinuationToken(BsonDocument scope, string sessionId, long lastSequence)
    {
        string scopeDiscriminator = scope["scope_discriminator"].AsString;
        string payload = string.Join(
            "|", ContinuationTokenVersion, scopeDiscriminator, sessionId, lastSequence.ToString(CultureInfo.InvariantCulture));
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = HMACSHA256.HashData(DeriveTokenKey(scopeDiscriminator), payloadBytes);
        return Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);
    }

    private static long DecodeContinuationToken(BsonDocument scope, string sessionId, string token)
    {
        string scopeDiscriminator = scope["scope_discriminator"].AsString;
        try
        {
            string[] parts = token.Split('.');
            if (parts.Length != 2)
            {
                throw InvalidTokenException();
            }

            byte[] payloadBytes = Base64UrlDecode(parts[0]);
            byte[] signature = Base64UrlDecode(parts[1]);
            byte[] expectedSignature = HMACSHA256.HashData(DeriveTokenKey(scopeDiscriminator), payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(signature, expectedSignature))
            {
                throw InvalidTokenException();
            }

            string[] fields = Encoding.UTF8.GetString(payloadBytes).Split('|');
            if (fields.Length != 4 ||
                fields[0] != ContinuationTokenVersion ||
                !string.Equals(fields[1], scopeDiscriminator, StringComparison.Ordinal) ||
                !string.Equals(fields[2], sessionId, StringComparison.Ordinal) ||
                !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long sequence))
            {
                throw InvalidTokenException();
            }

            return sequence;
        }
        catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException)
        {
            throw InvalidTokenException(exception);
        }
    }

    private static byte[] DeriveTokenKey(string scopeDiscriminator) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"checkpoint-continuation-token|{scopeDiscriminator}"));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }

    private static MongoDBConfigurationException InvalidTokenException(Exception? innerException = null) =>
        innerException is null
            ? new MongoDBConfigurationException(
                "Invalid or tampered Workflow Checkpoint Store continuation token.")
            : new MongoDBConfigurationException(
                "Invalid or tampered Workflow Checkpoint Store continuation token.", innerException);

    private static BsonDocument ValidateIndex(
        IReadOnlyList<BsonDocument> indexes,
        string name,
        IReadOnlyList<string> expectedKeys,
        bool expectedUnique)
    {
        BsonDocument? index = indexes.FirstOrDefault(candidate => candidate.GetValue("name", "") == name);
        if (index is null)
        {
            throw new MongoDBIndexMissingException(
                $"Required regular index '{name}' is missing; run EnsureIndexesAsync.");
        }

        if (!index.TryGetValue("key", out BsonValue keys) ||
            !keys.IsBsonDocument ||
            !keys.AsBsonDocument.Names.SequenceEqual(expectedKeys, StringComparer.Ordinal) ||
            keys.AsBsonDocument.Values.Any(value => value.ToInt32() != 1) ||
            index.GetValue("unique", false).ToBoolean() != expectedUnique)
        {
            throw new MongoDBIndexMismatchException(
                $"Regular index '{name}' does not match the required Workflow Checkpoint Store definition.");
        }

        return index;
    }

    private static async Task<T> WithDeadlineAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan? timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (timeout is null)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout.Value);
        try
        {
            return await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MongoDBTimeoutException(timeoutMessage, exception);
        }
    }
}
