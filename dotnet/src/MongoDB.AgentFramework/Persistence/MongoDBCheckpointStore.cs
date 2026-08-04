using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.AgentFramework.Internal;
using MongoDB.AgentFramework.Internal.Observability;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Buffers.Binary;
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
    private const byte ContinuationTokenFormatVersion = 1;

    /// <summary>
    /// The internal page size <see cref="RetrieveIndexAsync"/> uses to bound each individual query it issues
    /// while enumerating a session's full checkpoint index. Distinct from the caller-supplied
    /// <c>limit</c> the public <see cref="ListCheckpointsAsync"/> facade accepts.
    /// </summary>
    private const int RetrieveIndexPageSize = 1_000;

    /// <summary>
    /// Partial filter shared by both regular lookup indexes: scopes each to checkpoint documents only, so
    /// neither index ever includes the <c>sequence_counter</c> pseudo-documents that intentionally share this
    /// collection.
    /// </summary>
    private static readonly BsonDocument CheckpointOnlyPartialFilter = new("doc_type", CheckpointDocType);

    /// <summary>
    /// Partial filter for the TTL index: both checkpoint-document isolation (never a sequence counter, which
    /// has no <c>expires_at</c> field) AND an actual BSON date <c>expires_at</c> (never a checkpoint that was
    /// written with no expiration, whose <c>expires_at</c> is <see cref="BsonNull"/>) must hold together.
    /// </summary>
    private static readonly BsonDocument CheckpointExpirationTtlPartialFilter = new()
    {
        { "doc_type", CheckpointDocType },
        { "expires_at", new BsonDocument("$type", "date") },
    };

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly MongoDBCheckpointStoreOptions _options;
    private readonly OwnedResource<IMongoClient>? _client;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<MongoDBCheckpointStore> _logger;

    // Defensively copied out of _options.ContinuationTokenSigningKey at construction so a caller that mutates
    // its original array afterward cannot change this store's effective signing key.
    private readonly byte[] _continuationTokenSigningKey;

    /// <summary>Creates a store over an injected collection, which remains caller-owned.</summary>
    /// <remarks>
    /// This overload's exact parameter signature (no <see cref="ILogger{TCategoryName}"/> parameter) is a binary
    /// compatibility surface: it must never gain a new parameter, including an optional one, because a caller
    /// already compiled against it resolves default argument values at its own compile time, not this callee's.
    /// Use the sibling overload accepting an explicit <see cref="ILogger{TCategoryName}"/> for structured operation
    /// telemetry. See docs/development/observability-security/dotnet-telemetry.md.
    /// </remarks>
    public MongoDBCheckpointStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBCheckpointStoreOptions options)
        : this(collection, options, logger: null)
    {
    }

    /// <summary>
    /// Creates a store over an injected collection, which remains caller-owned, with an explicit logger for
    /// structured operation telemetry. See docs/development/observability-security/dotnet-telemetry.md.
    /// </summary>
    public MongoDBCheckpointStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBCheckpointStoreOptions options,
        ILogger<MongoDBCheckpointStore>? logger)
        : this(collection, options, DefaultResolvedFrameworkAssemblyVersionProvider, DefaultClock, logger)
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
        Func<Version> resolvedFrameworkAssemblyVersionProvider,
        ILogger<MongoDBCheckpointStore>? logger = null)
        : this(collection, options, resolvedFrameworkAssemblyVersionProvider, DefaultClock, logger)
    {
    }

    /// <summary>Test-only seam allowing "now" to be injected instead of <see cref="DateTimeOffset.UtcNow"/>.</summary>
    internal MongoDBCheckpointStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBCheckpointStoreOptions options,
        Func<DateTimeOffset> clock,
        ILogger<MongoDBCheckpointStore>? logger = null)
        : this(collection, options, DefaultResolvedFrameworkAssemblyVersionProvider, clock, logger)
    {
    }

    /// <summary>Test-only seam allowing both the resolved framework assembly version and "now" to be injected.</summary>
    internal MongoDBCheckpointStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBCheckpointStoreOptions options,
        Func<Version> resolvedFrameworkAssemblyVersionProvider,
        Func<DateTimeOffset> clock,
        ILogger<MongoDBCheckpointStore>? logger = null)
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
        _continuationTokenSigningKey = (byte[])options.ContinuationTokenSigningKey.Clone();
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _clock = clock;
        _logger = logger ?? NullLogger<MongoDBCheckpointStore>.Instance;
    }

    /// <summary>Creates a store over an injected database, which remains caller-owned.</summary>
    /// <remarks>See the collection constructor's remarks on why this overload's signature must stay exact.</remarks>
    public MongoDBCheckpointStore(
        IMongoDatabase database,
        string collectionName,
        MongoDBCheckpointStoreOptions options)
        : this(database, collectionName, options, logger: null)
    {
    }

    /// <summary>
    /// Creates a store over an injected database, which remains caller-owned, with an explicit logger for
    /// structured operation telemetry.
    /// </summary>
    public MongoDBCheckpointStore(
        IMongoDatabase database,
        string collectionName,
        MongoDBCheckpointStoreOptions options,
        ILogger<MongoDBCheckpointStore>? logger)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database))).GetCollection<BsonDocument>(
                MongoDBCheckpointStoreOptions.RequireText(collectionName, nameof(collectionName))),
            options,
            logger)
    {
    }

    /// <summary>Creates a store over an injected client, which remains caller-owned.</summary>
    /// <remarks>See the collection constructor's remarks on why this overload's signature must stay exact.</remarks>
    public MongoDBCheckpointStore(
        IMongoClient client,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options)
        : this(client, databaseName, collectionName, options, logger: null)
    {
    }

    /// <summary>
    /// Creates a store over an injected client, which remains caller-owned, with an explicit logger for
    /// structured operation telemetry.
    /// </summary>
    public MongoDBCheckpointStore(
        IMongoClient client,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options,
        ILogger<MongoDBCheckpointStore>? logger)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client))).GetDatabase(
                MongoDBCheckpointStoreOptions.RequireText(databaseName, nameof(databaseName))),
            collectionName,
            options,
            logger)
    {
    }

    /// <summary>Creates a provider-owned client from a connection string.</summary>
    /// <remarks>See the collection constructor's remarks on why this overload's signature must stay exact.</remarks>
    public MongoDBCheckpointStore(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options)
        : this(connectionString, databaseName, collectionName, options, logger: null)
    {
    }

    /// <summary>
    /// Creates a provider-owned client from a connection string, with an explicit logger for structured operation
    /// telemetry.
    /// </summary>
    public MongoDBCheckpointStore(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options,
        ILogger<MongoDBCheckpointStore>? logger)
        : this(connectionString, databaseName, collectionName, options, clientFactory: null, logger)
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
        Func<string, IMongoClient>? clientFactory,
        ILogger<MongoDBCheckpointStore>? logger = null)
        : this(connectionString, databaseName, collectionName, options, clientFactory,
              DefaultResolvedFrameworkAssemblyVersionProvider, logger)
    {
    }

    /// <summary>Test-only seam additionally allowing the resolved framework assembly version to be injected.</summary>
    internal MongoDBCheckpointStore(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBCheckpointStoreOptions options,
        Func<string, IMongoClient>? clientFactory,
        Func<Version> resolvedFrameworkAssemblyVersionProvider,
        ILogger<MongoDBCheckpointStore>? logger = null)
        : this(Connect(
            connectionString, databaseName, collectionName, options, clientFactory,
            resolvedFrameworkAssemblyVersionProvider), logger)
    {
    }

    private MongoDBCheckpointStore(
        (OwnedResource<IMongoClient> Client,
         IMongoCollection<BsonDocument> Collection,
         MongoDBCheckpointStoreOptions Options,
         Func<Version> VersionProvider) connected,
        ILogger<MongoDBCheckpointStore>? logger = null)
        : this(connected.Collection, connected.Options, connected.VersionProvider, logger)
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
    /// if any (the base contract has no expiry parameter), and applies this store's configured
    /// <see cref="MongoDBCheckpointStoreOptions.PersistenceTimeout"/> even though the base contract gives no
    /// <see cref="CancellationToken"/> to observe an external one -- a hung write still fails with a stable
    /// <see cref="MongoDBTimeoutException"/> rather than blocking the caller indefinitely.
    /// </remarks>
    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId,
        JsonElement value,
        CheckpointInfo? parent = null)
    {
        string checkpointId = Guid.NewGuid().ToString("N");
        MongoDBCheckpointRecord record = await SaveCheckpointCoreAsync(
            sessionId, checkpointId, value, parent?.CheckpointId, expiresAt: null, CancellationToken.None)
            .ConfigureAwait(false);
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
                "No checkpoint exists at the requested authorized identity.")
            : record.Payload;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns every matching checkpoint (the base contract is unbounded), in ascending, monotonic
    /// <c>sequence</c> order -- never timestamp order -- so framework callers such as
    /// <c>CheckpointManager.GetLatestCheckpointAsync</c> that rely on this ordering to find the head checkpoint
    /// observe correct results. Internally paged in bounded batches to avoid one unbounded query, but exactly
    /// one <see cref="MongoDBCheckpointStoreOptions.RetrievalTimeout"/> deadline governs the <em>entire</em>
    /// multi-page operation -- the deadline is established once, before the first page is fetched, and is never
    /// reset as later pages are fetched, so a slow or stalled page cannot silently grant the operation a fresh
    /// full timeout budget. A stable upper <c>sequence</c> bound is also captured once, from the scoped latest
    /// committed checkpoint at that instant, before the first page is fetched; every page is filtered to that
    /// snapshot bound (inclusive), so checkpoints committed by other writers <em>during</em> this enumeration
    /// are excluded rather than making the operation unbounded or returning an inconsistent, ever-growing
    /// result.
    /// </remarks>
    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId,
        CheckpointInfo? withParent = null) =>
        await MongoDBTelemetry.TrackAsync(
            _logger,
            MongoDBTelemetryFeature.CheckpointStore,
            MongoDBTelemetryOperation.List,
            mode: null,
            () => RetrieveIndexInnerAsync(sessionId, withParent),
            static results => new MongoDBTelemetryResult(
                results.Count > 0 ? MongoDBTelemetryOutcome.Success : MongoDBTelemetryOutcome.Empty,
                results.Count,
                CandidateBucket: null),
            CancellationToken.None).ConfigureAwait(false);

    private async Task<IReadOnlyList<CheckpointInfo>> RetrieveIndexInnerAsync(
        string sessionId,
        CheckpointInfo? withParent)
    {
        BsonDocument scope = Scope(sessionId);
        return await WithDeadlineAsync(
            async token =>
            {
                try
                {
                    long? upperBound = await FindMaxSequenceAsync(scope, sessionId, token).ConfigureAwait(false);
                    var results = new List<CheckpointInfo>();
                    if (upperBound is null)
                    {
                        return (IReadOnlyList<CheckpointInfo>)results;
                    }

                    long? afterSequence = null;
                    bool hasMore;
                    do
                    {
                        (IReadOnlyList<BsonDocument> documents, hasMore) = await FindCheckpointPageAsync(
                            scope, sessionId, afterSequence, upperBound, RetrieveIndexPageSize, token)
                            .ConfigureAwait(false);
                        foreach (BsonDocument document in documents)
                        {
                            MongoDBCheckpointSummary summary = ToSummary(document);
                            if (withParent is null || summary.ParentCheckpointId == withParent.CheckpointId)
                            {
                                results.Add(new CheckpointInfo(summary.SessionId, summary.CheckpointId));
                            }
                        }

                        if (documents.Count > 0)
                        {
                            afterSequence = documents[^1]["sequence"].ToInt64();
                        }
                    } while (hasMore);

                    return (IReadOnlyList<CheckpointInfo>)results;
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
            CancellationToken.None).ConfigureAwait(false);
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
        return await SaveCheckpointCoreAsync(
            sessionId, checkpointId, payload, parentCheckpointId, expiresAt, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Loads a checkpoint by its explicit identifier, or <see langword="null"/> if absent.</summary>
    public Task<MongoDBCheckpointRecord?> LoadCheckpointAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default) =>
        MongoDBTelemetry.TrackAsync(
            _logger,
            MongoDBTelemetryFeature.CheckpointStore,
            MongoDBTelemetryOperation.Load,
            mode: null,
            () => LoadCheckpointInnerAsync(sessionId, checkpointId, cancellationToken),
            static record => new MongoDBTelemetryResult(
                record is null ? MongoDBTelemetryOutcome.Empty : MongoDBTelemetryOutcome.Success,
                record is null ? 0 : 1,
                CandidateBucket: null),
            cancellationToken);

    private async Task<MongoDBCheckpointRecord?> LoadCheckpointInnerAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken)
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
    public Task<MongoDBCheckpointRecord?> GetLatestCheckpointAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        MongoDBTelemetry.TrackAsync(
            _logger,
            MongoDBTelemetryFeature.CheckpointStore,
            MongoDBTelemetryOperation.Load,
            mode: null,
            () => GetLatestCheckpointInnerAsync(sessionId, cancellationToken),
            static record => new MongoDBTelemetryResult(
                record is null ? MongoDBTelemetryOutcome.Empty : MongoDBTelemetryOutcome.Success,
                record is null ? 0 : 1,
                CandidateBucket: null),
            cancellationToken);

    private async Task<MongoDBCheckpointRecord?> GetLatestCheckpointInnerAsync(
        string sessionId,
        CancellationToken cancellationToken)
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
    public Task<MongoDBCheckpointPage> ListCheckpointsAsync(
        string sessionId,
        int limit,
        string? continuationToken = null,
        CancellationToken cancellationToken = default) =>
        MongoDBTelemetry.TrackAsync(
            _logger,
            MongoDBTelemetryFeature.CheckpointStore,
            MongoDBTelemetryOperation.List,
            mode: null,
            () => ListCheckpointsInnerAsync(sessionId, limit, continuationToken, cancellationToken),
            static page => new MongoDBTelemetryResult(
                page.Items.Count > 0 ? MongoDBTelemetryOutcome.Success : MongoDBTelemetryOutcome.Empty,
                page.Items.Count,
                CandidateBucket: null),
            cancellationToken);

    private async Task<MongoDBCheckpointPage> ListCheckpointsInnerAsync(
        string sessionId,
        int limit,
        string? continuationToken,
        CancellationToken cancellationToken)
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
                    (IReadOnlyList<BsonDocument> documents, bool hasMore) = await FindCheckpointPageAsync(
                        scope, sessionId, afterSequence, maxSequenceInclusive: null, limit, token)
                        .ConfigureAwait(false);
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
    public Task<bool> DeleteCheckpointAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken = default) =>
        MongoDBTelemetry.TrackAsync(
            _logger,
            MongoDBTelemetryFeature.CheckpointStore,
            MongoDBTelemetryOperation.Delete,
            mode: null,
            () => DeleteCheckpointInnerAsync(sessionId, checkpointId, cancellationToken),
            static deleted => new MongoDBTelemetryResult(
                deleted ? MongoDBTelemetryOutcome.Success : MongoDBTelemetryOutcome.Empty,
                deleted ? 1 : 0,
                CandidateBucket: null),
            cancellationToken);

    private async Task<bool> DeleteCheckpointInnerAsync(
        string sessionId,
        string checkpointId,
        CancellationToken cancellationToken)
    {
        BsonDocument scope = Scope(sessionId);
        MongoDBCheckpointStoreOptions.RequireText(checkpointId, nameof(checkpointId));
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                try
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
                    throw new MongoDBPersistenceException(
                        "MongoDB Workflow Checkpoint Store delete failed.",
                        exception);
                }
            },
            _options.PersistenceTimeout,
            "MongoDB Workflow Checkpoint Store persistence deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicitly provisions the required regular lookup indexes and the optional TTL index. Never called
    /// implicitly during construction, saves, or retrieval.
    /// </summary>
    public Task<IReadOnlyList<string>> EnsureIndexesAsync(CancellationToken cancellationToken = default) =>
        MongoDBTelemetry.TrackAsync(
            _logger,
            MongoDBTelemetryFeature.CheckpointStore,
            MongoDBTelemetryOperation.EnsureIndex,
            mode: null,
            () => EnsureIndexesInnerAsync(cancellationToken),
            static _ => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Success, null, null),
            cancellationToken);

    private async Task<IReadOnlyList<string>> EnsureIndexesInnerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                    PartialFilterExpression = CheckpointOnlyPartialFilter,
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
                    PartialFilterExpression = CheckpointOnlyPartialFilter,
                }),
            new(
                Builders<BsonDocument>.IndexKeys.Ascending("expires_at"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "checkpoint_expiration_ttl",
                    ExpireAfter = TimeSpan.Zero,
                    PartialFilterExpression = CheckpointExpirationTtlPartialFilter,
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
    public Task ValidateIndexesAsync(CancellationToken cancellationToken = default) =>
        MongoDBTelemetry.TrackAsync(
            _logger,
            MongoDBTelemetryFeature.CheckpointStore,
            MongoDBTelemetryOperation.ValidateIndex,
            mode: null,
            () => ValidateIndexesInnerAsync(cancellationToken),
            static () => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Success, null, null),
            cancellationToken);

    private async Task ValidateIndexesInnerAsync(CancellationToken cancellationToken)
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
                expectedUnique: true,
                CheckpointOnlyPartialFilter);
            ValidateIndex(
                indexes,
                "checkpoint_sequence_lookup",
                ["tenant_id", "workflow_id", "session_id", "sequence"],
                expectedUnique: false,
                CheckpointOnlyPartialFilter);
            BsonDocument ttl = ValidateIndex(
                indexes,
                "checkpoint_expiration_ttl",
                ["expires_at"],
                expectedUnique: false,
                CheckpointExpirationTtlPartialFilter);
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

    /// <summary>
    /// Instrumented once here so both the framework's <see cref="CreateCheckpointAsync"/> hook and the public
    /// <see cref="SaveCheckpointAsync"/> facade -- which both call this shared core -- emit exactly one
    /// telemetry activity/log per underlying persistence attempt, never a duplicate.
    /// </summary>
    private Task<MongoDBCheckpointRecord> SaveCheckpointCoreAsync(
        string sessionId,
        string checkpointId,
        JsonElement payload,
        string? parentCheckpointId,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken) =>
        MongoDBTelemetry.TrackAsync(
            _logger,
            MongoDBTelemetryFeature.CheckpointStore,
            MongoDBTelemetryOperation.Persist,
            mode: null,
            () => WithDeadlineAsync(
                token => SaveCheckpointCoreInnerAsync(
                    sessionId, checkpointId, payload, parentCheckpointId, expiresAt, token),
                _options.PersistenceTimeout,
                "MongoDB Workflow Checkpoint Store persistence deadline exceeded.",
                cancellationToken),
            static _ => new MongoDBTelemetryResult(MongoDBTelemetryOutcome.Success, 1, CandidateBucket: null),
            cancellationToken);

    private async Task<MongoDBCheckpointRecord> SaveCheckpointCoreInnerAsync(
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

        // Check for an existing checkpoint before opening a transaction, so a purely idempotent retry (the
        // common case) never opens a transaction or burns a sequence value.
        MongoDBCheckpointRecord? converged = await TryConvergeAsync(
            scope, sessionId, checkpointId, payloadBytes, parentCheckpointId, raceException: null, cancellationToken)
            .ConfigureAwait(false);
        if (converged is not null)
        {
            return converged;
        }

        IClientSessionHandle? session = null;
        try
        {
            session = await _collection.Database.Client.StartSessionAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var transactionOptions = new TransactionOptions(writeConcern: WriteConcern.WMajority);

            // Sequence allocation and the checkpoint insert commit atomically together inside one transaction,
            // so sequence genuinely reflects committed order under cross-process concurrency, not merely
            // allocation order. WithTransactionAsync's own retry loop (per official MongoDB driver guidance)
            // handles TransientTransactionError/UnknownTransactionCommitResult within this cancellation token.
            return await session.WithTransactionAsync(
                async (txnSession, token) =>
                {
                    long sequence = await AllocateSequenceAsync(txnSession, scope, sessionId, token)
                        .ConfigureAwait(false);
                    BsonDocument candidate = BuildCheckpointDocument(
                        scope, sessionId, checkpointId, parentCheckpointId, sequence, payloadBytes, now,
                        effectiveExpiresAt);
                    await _collection.InsertOneAsync(txnSession, candidate, cancellationToken: token)
                        .ConfigureAwait(false);
                    return ToRecord(candidate);
                },
                transactionOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException exception) when (IsDuplicateKey(exception))
        {
            // A genuine race between two concurrent first writers for the same identifier that the pre-check
            // above did not observe. The aborted transaction burned no sequence value; the losing writer
            // re-fetches the winner's document and applies the same converge-or-conflict comparison.
            MongoDBCheckpointRecord? raced = await TryConvergeAsync(
                scope, sessionId, checkpointId, payloadBytes, parentCheckpointId, exception, cancellationToken)
                .ConfigureAwait(false);
            if (raced is not null)
            {
                return raced;
            }

            throw ConflictException(exception);
        }
        catch (MongoException exception) when (IsTransactionsUnsupported(exception))
        {
            throw new MongoDBCapabilityException(
                "MongoDB Workflow Checkpoint Store requires a deployment that supports multi-document " +
                "transactions (a replica set or sharded cluster) so that monotonic sequence allocation and the " +
                "checkpoint write commit atomically. This deployment rejected the transaction as unsupported, " +
                "so no ordering guarantee could be honored; the checkpoint was not written. Deploy against a " +
                "replica set or sharded cluster, or mongos, to use this store.",
                exception);
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
            throw new MongoDBPersistenceException(
                "MongoDB Workflow Checkpoint Store persistence failed.",
                exception);
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>
    /// Re-reads the current state at this checkpoint's authorized identity to determine whether a write attempt
    /// (either a pre-transaction fast path or a post-abort race resolution) can converge on already-committed,
    /// identical content instead of writing again. Returns <see langword="null"/> when nothing exists yet (the
    /// caller should proceed with a real write); throws if something exists but with an unsupported schema or
    /// different content. Wraps any non-cancellation driver failure from its own read in a stable
    /// <see cref="MongoDBPersistenceException"/> so both call sites (one outside, one inside a catch handler)
    /// never propagate a raw driver exception.
    /// </summary>
    private async Task<MongoDBCheckpointRecord?> TryConvergeAsync(
        BsonDocument scope,
        string sessionId,
        string checkpointId,
        BsonBinaryData payloadBytes,
        string? parentCheckpointId,
        Exception? raceException,
        CancellationToken cancellationToken)
    {
        BsonDocument? existing;
        try
        {
            existing = await FindOneAsync(IdentityFilter(scope, sessionId, checkpointId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw new MongoDBPersistenceException(
                "MongoDB Workflow Checkpoint Store persistence failed.",
                exception);
        }

        if (existing is null)
        {
            return null;
        }

        if (!HasCompatibleSchema(existing))
        {
            throw IncompatibleSchemaException();
        }

        if (ContentEquals(existing, payloadBytes, parentCheckpointId))
        {
            return ToRecord(existing);
        }

        throw ConflictException(raceException);
    }

    private async Task<long> AllocateSequenceAsync(
        IClientSessionHandle session,
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
            session,
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

    /// <summary>
    /// Returns the greatest committed <c>sequence</c> in scope, or <see langword="null"/> if no checkpoint
    /// exists. Reads only the raw <c>sequence</c> field -- unlike <see cref="ToRecord"/>/<see cref="ToSummary"/>
    /// it does not validate <c>schema_version</c>, since it exists purely to capture a snapshot upper bound for
    /// <see cref="RetrieveIndexAsync"/>'s enumeration, not to surface that document's content.
    /// </summary>
    private async Task<long?> FindMaxSequenceAsync(
        BsonDocument scope,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var findOptions = new FindOptions<BsonDocument, BsonDocument>
        {
            Sort = Builders<BsonDocument>.Sort.Descending("sequence"),
            Limit = 1,
        };
        using IAsyncCursor<BsonDocument> cursor = await _collection.FindAsync(
            ScopeSessionFilter(scope, sessionId),
            findOptions,
            cancellationToken).ConfigureAwait(false);
        BsonDocument? document = await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false)
            ? cursor.Current.FirstOrDefault()
            : null;
        return document?["sequence"].ToInt64();
    }

    /// <summary>
    /// Fetches one bounded, ascending-<c>sequence</c>-ordered page of raw checkpoint documents, shared by the
    /// public <see cref="ListCheckpointsAsync"/> facade (<paramref name="maxSequenceInclusive"/> is
    /// <see langword="null"/>, so it never excludes newly committed checkpoints) and
    /// <see cref="RetrieveIndexAsync"/>'s internal multi-page loop (<paramref name="maxSequenceInclusive"/> is
    /// the snapshot upper bound captured once before paging begins, so later pages never observe checkpoints
    /// committed after that snapshot).
    /// </summary>
    private async Task<(IReadOnlyList<BsonDocument> Documents, bool HasMore)> FindCheckpointPageAsync(
        BsonDocument scope,
        string sessionId,
        long? afterSequenceExclusive,
        long? maxSequenceInclusive,
        int limit,
        CancellationToken cancellationToken)
    {
        FilterDefinition<BsonDocument> filter = ScopeSessionFilter(scope, sessionId);
        if (afterSequenceExclusive is { } after)
        {
            filter &= Builders<BsonDocument>.Filter.Gt("sequence", after);
        }

        if (maxSequenceInclusive is { } max)
        {
            filter &= Builders<BsonDocument>.Filter.Lte("sequence", max);
        }

        var findOptions = new FindOptions<BsonDocument, BsonDocument>
        {
            Sort = Builders<BsonDocument>.Sort.Ascending("sequence"),
            Limit = limit + 1,
        };
        using IAsyncCursor<BsonDocument> cursor = await _collection.FindAsync(filter, findOptions, cancellationToken)
            .ConfigureAwait(false);
        var documents = new List<BsonDocument>();
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.AddRange(cursor.Current);
        }

        bool hasMore = documents.Count > limit;
        if (hasMore)
        {
            documents.RemoveAt(documents.Count - 1);
        }

        return (documents, hasMore);
    }

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

    private static MongoDBConcurrencyException ConflictException(Exception? innerException = null)
    {
        const string Message =
            "A checkpoint with this identifier already exists in scope with a different payload or parent " +
            "lineage. Checkpoints are immutable historical records; use a new checkpoint id for a new " +
            "checkpoint.";
        return innerException is null
            ? new MongoDBConcurrencyException(Message)
            : new MongoDBConcurrencyException(Message, innerException);
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

    /// <summary>
    /// Detects the specific MongoDB server error raised when multi-document transactions are attempted against
    /// a deployment that does not support them (a standalone <c>mongod</c>) -- server error code 20
    /// (<c>IllegalOperation</c>) with a message containing "Transaction numbers". Detecting this precisely lets
    /// the store fail with an explicit <see cref="MongoDBCapabilityException"/> rather than silently claiming
    /// an ordering guarantee the deployment cannot provide.
    /// </summary>
    private static bool IsTransactionsUnsupported(MongoException exception) =>
        exception is MongoCommandException { Code: 20 } commandException &&
        commandException.ErrorMessage.Contains("Transaction numbers", StringComparison.OrdinalIgnoreCase);

    private static string CheckpointDocumentId(BsonDocument scope, string sessionId, string checkpointId) =>
        Hash(FrameFields("checkpoint", scope["scope_discriminator"].AsString, sessionId, checkpointId));

    private static string SequenceCounterDocumentId(BsonDocument scope, string sessionId) =>
        Hash(FrameFields("sequence_counter", scope["scope_discriminator"].AsString, sessionId));

    private static string Hash(byte[] framedValue) =>
        Convert.ToHexString(SHA256.HashData(framedValue)).ToLowerInvariant();

    /// <summary>
    /// Canonically frames an ordered sequence of string components as length-prefixed binary -- never
    /// delimiter-joined text -- before hashing or signing. Session, checkpoint, and parent-checkpoint
    /// identifiers are arbitrary caller-controlled opaque strings that may contain any character, including any
    /// delimiter (for example a literal <c>|</c>) this store might otherwise have chosen to join components
    /// with; delimiter-joining would let a crafted identifier make two logically distinct component sequences
    /// collide onto the same document ID, cache key, or signed payload. Each component is instead framed as a
    /// big-endian 4-byte UTF-8 byte length followed by its exact UTF-8 bytes, which is unambiguous and injective
    /// regardless of component content.
    /// </summary>
    private static byte[] FrameFields(params string[] components)
    {
        using var stream = new MemoryStream();
        foreach (string component in components)
        {
            WriteLengthPrefixed(stream, Encoding.UTF8.GetBytes(component));
        }

        return stream.ToArray();
    }

    private static void WriteLengthPrefixed(Stream stream, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void WriteOptionalLengthPrefixed(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }

        stream.WriteByte(1);
        WriteLengthPrefixed(stream, Encoding.UTF8.GetBytes(value));
    }

    private static string CanonicalScopeDiscriminator(string? tenantId, string workflowId)
    {
        using var stream = new MemoryStream();
        WriteLengthPrefixed(stream, Encoding.UTF8.GetBytes("workflow-scope"));
        WriteOptionalLengthPrefixed(stream, tenantId);
        WriteLengthPrefixed(stream, Encoding.UTF8.GetBytes(workflowId));
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>
    /// Encodes a scoped, versioned, self-verifying continuation token. The payload is length-prefixed binary
    /// (never delimiter-joined) so an opaque session ID may contain any byte sequence without risk of field
    /// collision, and the HMAC signature is keyed by this store's configured, genuinely random
    /// <see cref="MongoDBCheckpointStoreOptions.ContinuationTokenSigningKey"/> (combined with this store's own
    /// scope for domain separation) -- never derived solely from token-visible data -- so a token cannot be
    /// forged or reused across a differently scoped store without knowledge of the secret key.
    /// </summary>
    private string EncodeContinuationToken(BsonDocument scope, string sessionId, long lastSequence)
    {
        string scopeDiscriminator = scope["scope_discriminator"].AsString;
        byte[] payloadBytes = FrameContinuationTokenPayload(scopeDiscriminator, sessionId, lastSequence);
        byte[] signature = HMACSHA256.HashData(DeriveTokenKey(scopeDiscriminator), payloadBytes);
        return Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);
    }

    private long DecodeContinuationToken(BsonDocument scope, string sessionId, string token)
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
            if (signature.Length != expectedSignature.Length ||
                !CryptographicOperations.FixedTimeEquals(signature, expectedSignature))
            {
                throw InvalidTokenException();
            }

            if (!TryParseContinuationTokenPayload(
                    payloadBytes, out byte version, out string decodedScope, out string decodedSessionId, out long sequence) ||
                version != ContinuationTokenFormatVersion ||
                !string.Equals(decodedScope, scopeDiscriminator, StringComparison.Ordinal) ||
                !string.Equals(decodedSessionId, sessionId, StringComparison.Ordinal))
            {
                throw InvalidTokenException();
            }

            return sequence;
        }
        catch (Exception exception) when (
            exception is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw InvalidTokenException(exception);
        }
    }

    private static byte[] FrameContinuationTokenPayload(string scopeDiscriminator, string sessionId, long lastSequence)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(ContinuationTokenFormatVersion);
        WriteLengthPrefixed(stream, Encoding.UTF8.GetBytes(scopeDiscriminator));
        WriteLengthPrefixed(stream, Encoding.UTF8.GetBytes(sessionId));
        Span<byte> sequenceBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(sequenceBytes, lastSequence);
        stream.Write(sequenceBytes);
        return stream.ToArray();
    }

    private static bool TryParseContinuationTokenPayload(
        byte[] payloadBytes,
        out byte version,
        out string scopeDiscriminator,
        out string sessionId,
        out long sequence)
    {
        version = 0;
        scopeDiscriminator = string.Empty;
        sessionId = string.Empty;
        sequence = 0L;
        int offset = 0;
        if (!TryReadByte(payloadBytes, ref offset, out version) ||
            !TryReadLengthPrefixedUtf8(payloadBytes, ref offset, out scopeDiscriminator) ||
            !TryReadLengthPrefixedUtf8(payloadBytes, ref offset, out sessionId) ||
            payloadBytes.Length - offset != 8)
        {
            return false;
        }

        sequence = BinaryPrimitives.ReadInt64BigEndian(payloadBytes.AsSpan(offset, 8));
        offset += 8;
        return offset == payloadBytes.Length;
    }

    private static bool TryReadByte(byte[] buffer, ref int offset, out byte value)
    {
        if (offset >= buffer.Length)
        {
            value = 0;
            return false;
        }

        value = buffer[offset];
        offset++;
        return true;
    }

    private static bool TryReadLengthPrefixedUtf8(byte[] buffer, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset + 4 > buffer.Length)
        {
            return false;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        offset += 4;
        if (length > int.MaxValue || offset + length > buffer.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(buffer, offset, (int)length);
        offset += (int)length;
        return true;
    }

    /// <summary>
    /// Derives the effective continuation-token HMAC key by combining the configured, genuinely random
    /// <see cref="MongoDBCheckpointStoreOptions.ContinuationTokenSigningKey"/> secret with this store's scope
    /// discriminator for domain separation (so per-scope subkeys are cryptographically independent even though
    /// they share one configured secret) -- the key is never derived from token-visible data alone.
    /// </summary>
    private byte[] DeriveTokenKey(string scopeDiscriminator) =>
        HMACSHA256.HashData(
            _continuationTokenSigningKey,
            FrameFields(
                "checkpoint-continuation-token",
                ContinuationTokenFormatVersion.ToString(CultureInfo.InvariantCulture),
                scopeDiscriminator));

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
        bool expectedUnique,
        BsonDocument expectedPartialFilterExpression)
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

        if (!index.TryGetValue("partialFilterExpression", out BsonValue partialFilter) ||
            !partialFilter.IsBsonDocument ||
            !partialFilter.AsBsonDocument.Equals(expectedPartialFilterExpression))
        {
            throw new MongoDBIndexMismatchException(
                $"Regular index '{name}' does not match the required Workflow Checkpoint Store definition: " +
                "its partialFilterExpression is missing or does not exactly match the required " +
                "document-type isolation filter.");
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
