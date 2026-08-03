using Microsoft.Agents.AI;
using MongoDB.AgentFramework.Internal;
using MongoDB.AgentFramework.Internal.Persistence;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MongoDB.AgentFramework;

/// <summary>
/// Persists a complete, versioned, authorized <see cref="AgentSession"/> snapshot for stateless agent hosting.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.Agents.AI.Abstractions</c> (verified at the pinned floor 1.13.0, and unchanged through the
/// latest published 1.16.0; see docs/development/persistence/dotnet-contract-research.md) does not expose a
/// public session-hosting persistence contract for a MongoDB implementation to satisfy. This type is therefore a
/// narrow facade over the public <see cref="AgentSession"/> serialization API
/// (<see cref="AIAgent.SerializeSessionAsync"/>/<see cref="AIAgent.DeserializeSessionAsync"/>) rather than an
/// implementation of any framework interface -- there is no framework interface to implement. Its methods
/// deliberately require the originating <see cref="AIAgent"/> because <see cref="AgentSession"/> JSON shape is
/// agent-defined. See <see cref="Internal.Persistence.IAgentSessionCodec"/> for the seam that would let a future
/// package version add a dedicated adapter without changing this store's public methods or its BSON schema.
/// </para>
/// <para>
/// This build only supports the resolved <c>Microsoft.Agents.AI.Abstractions</c> assembly versions in
/// <c>[<see cref="MinimumSupportedFrameworkAssemblyVersion"/>, <see cref="MaximumSupportedFrameworkAssemblyVersionExclusive"/>)</c>;
/// every constructor validates the resolved assembly version and throws <see cref="MongoDBConfigurationException"/>
/// for any other version rather than risk silently writing an envelope an unverified framework version would
/// deserialize incompatibly. Stored documents also carry explicit <c>schema_version</c>/<c>framework_version</c>
/// markers; a document written by an unsupported version is never read, updated, or deleted -- see
/// docs/development/persistence/dotnet-session-store-migration.md for the required manual remediation.
/// </para>
/// </remarks>
public sealed class MongoDBAgentSessionStore : IAsyncDisposable
{
    /// <summary>The stored MongoDB envelope schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The internal Agent Framework JSON envelope compatibility marker (not the NuGet package version).</summary>
    public const int FrameworkSerializationVersion = 1;

    /// <summary>
    /// The minimum resolved <c>Microsoft.Agents.AI.Abstractions</c> assembly version this build has verified
    /// (inclusive). See docs/development/persistence/dotnet-contract-research.md.
    /// </summary>
    internal static readonly Version MinimumSupportedFrameworkAssemblyVersion = new(1, 13, 0, 0);

    /// <summary>
    /// The upper bound (exclusive) of the resolved <c>Microsoft.Agents.AI.Abstractions</c> assembly version this
    /// build has verified. See docs/development/persistence/dotnet-contract-research.md.
    /// </summary>
    internal static readonly Version MaximumSupportedFrameworkAssemblyVersionExclusive = new(1, 17, 0, 0);

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly MongoDBAgentSessionStoreOptions _options;
    private readonly OwnedResource<IMongoClient>? _client;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a store over an injected collection, which remains caller-owned.</summary>
    public MongoDBAgentSessionStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBAgentSessionStoreOptions options)
        : this(collection, options, DefaultResolvedFrameworkAssemblyVersionProvider, DefaultClock)
    {
    }

    /// <summary>
    /// Test-only seam allowing the resolved framework assembly version to be injected instead of inspected from
    /// the loaded <see cref="AIAgent"/> assembly, so unsupported-version rejection is unit-testable without
    /// loading multiple real assembly versions side by side.
    /// </summary>
    internal MongoDBAgentSessionStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBAgentSessionStoreOptions options,
        Func<Version> resolvedFrameworkAssemblyVersionProvider)
        : this(collection, options, resolvedFrameworkAssemblyVersionProvider, DefaultClock)
    {
    }

    /// <summary>
    /// Test-only seam allowing "now" to be injected instead of <see cref="DateTimeOffset.UtcNow"/>, so
    /// default-expiration retry-convergence behavior across elapsed time (a retried <see cref="CreateAsync"/> or
    /// compare-and-swap <see cref="SetAsync"/> call whose default-derived candidate expiry is recomputed later
    /// than the persisted one) is unit-testable with a fake clock instead of a real sleep.
    /// </summary>
    internal MongoDBAgentSessionStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBAgentSessionStoreOptions options,
        Func<DateTimeOffset> clock)
        : this(collection, options, DefaultResolvedFrameworkAssemblyVersionProvider, clock)
    {
    }

    /// <summary>Test-only seam allowing both the resolved framework assembly version and "now" to be injected.</summary>
    internal MongoDBAgentSessionStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBAgentSessionStoreOptions options,
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
            ApplicationId = options.ApplicationId.Trim(),
            AgentId = options.AgentId.Trim(),
            UserId = options.UserId?.Trim(),
        };
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _clock = clock;
    }

    /// <summary>Creates a store over an injected database, which remains caller-owned.</summary>
    public MongoDBAgentSessionStore(
        IMongoDatabase database,
        string collectionName,
        MongoDBAgentSessionStoreOptions options)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database))).GetCollection<BsonDocument>(
                MongoDBAgentSessionStoreOptions.RequireText(collectionName, nameof(collectionName))),
            options)
    {
    }

    /// <summary>Creates a store over an injected client, which remains caller-owned.</summary>
    public MongoDBAgentSessionStore(
        IMongoClient client,
        string databaseName,
        string collectionName,
        MongoDBAgentSessionStoreOptions options)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client))).GetDatabase(
                MongoDBAgentSessionStoreOptions.RequireText(databaseName, nameof(databaseName))),
            collectionName,
            options)
    {
    }

    /// <summary>Creates a provider-owned client from a connection string.</summary>
    public MongoDBAgentSessionStore(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBAgentSessionStoreOptions options)
        : this(connectionString, databaseName, collectionName, options, clientFactory: null)
    {
    }

    /// <summary>
    /// Test-only seam mirroring <see cref="MongoClientFactory.FromConnectionString"/>'s existing
    /// <c>clientFactory</c> override. It exists solely so tests can substitute the underlying
    /// <see cref="IMongoClient"/> and prove that a construction failure occurring after the owned client is
    /// created (for example resolving the database/collection) still disposes it; it is internal because it is
    /// not part of the public surface.
    /// </summary>
    internal MongoDBAgentSessionStore(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBAgentSessionStoreOptions options,
        Func<string, IMongoClient>? clientFactory)
        : this(connectionString, databaseName, collectionName, options, clientFactory,
              DefaultResolvedFrameworkAssemblyVersionProvider)
    {
    }

    /// <summary>Test-only seam additionally allowing the resolved framework assembly version to be injected.</summary>
    internal MongoDBAgentSessionStore(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBAgentSessionStoreOptions options,
        Func<string, IMongoClient>? clientFactory,
        Func<Version> resolvedFrameworkAssemblyVersionProvider)
        : this(Connect(
            connectionString, databaseName, collectionName, options, clientFactory,
            resolvedFrameworkAssemblyVersionProvider))
    {
    }

    private MongoDBAgentSessionStore(
        (OwnedResource<IMongoClient> Client,
         IMongoCollection<BsonDocument> Collection,
         MongoDBAgentSessionStoreOptions Options,
         Func<Version> VersionProvider) connected)
        : this(connected.Collection, connected.Options, connected.VersionProvider)
    {
        _client = connected.Client;
    }

    /// <summary>
    /// Validates every constructor argument that does not require a MongoDB client -- options and the resolved
    /// framework assembly version -- entirely before creating an owned client. If this validated first and a
    /// chained constructor validated those requirements afterward instead, an invalid option or unsupported
    /// framework version would throw only after <see cref="MongoClientFactory.FromConnectionString"/> had already
    /// created a client, and since no <see cref="MongoDBAgentSessionStore"/> instance would ever exist to dispose
    /// it, that client would leak. Resolving the database/collection can still throw after the client exists (a
    /// real network-dependent step); this method disposes the client itself in that case, since it runs before
    /// any instance exists either.
    /// </summary>
    private static (OwnedResource<IMongoClient> Client,
        IMongoCollection<BsonDocument> Collection,
        MongoDBAgentSessionStoreOptions Options,
        Func<Version> VersionProvider) Connect(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBAgentSessionStoreOptions options,
        Func<string, IMongoClient>? clientFactory,
        Func<Version> resolvedFrameworkAssemblyVersionProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolvedFrameworkAssemblyVersionProvider);
        options.Validate();
        ValidateResolvedFrameworkAssemblyVersion(resolvedFrameworkAssemblyVersionProvider());
        string validDatabaseName = MongoDBAgentSessionStoreOptions.RequireText(databaseName, nameof(databaseName));
        string validCollectionName =
            MongoDBAgentSessionStoreOptions.RequireText(collectionName, nameof(collectionName));

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
        typeof(AIAgent).Assembly.GetName().Version
            ?? throw new MongoDBConfigurationException(
                "Unable to determine the resolved Microsoft.Agents.AI.Abstractions assembly version.");

    private static void ValidateResolvedFrameworkAssemblyVersion(Version resolvedVersion)
    {
        if (resolvedVersion < MinimumSupportedFrameworkAssemblyVersion ||
            resolvedVersion >= MaximumSupportedFrameworkAssemblyVersionExclusive)
        {
            throw new MongoDBConfigurationException(
                $"MongoDBAgentSessionStore has verified Microsoft.Agents.AI.Abstractions " +
                $"[{MinimumSupportedFrameworkAssemblyVersion},{MaximumSupportedFrameworkAssemblyVersionExclusive}) " +
                $"only (see docs/development/persistence/dotnet-contract-research.md), but the resolved " +
                $"assembly reports version {resolvedVersion}. Pin a verified " +
                "Microsoft.Agents.AI.Abstractions version, or re-run the compatibility verification in that " +
                "document and widen this range, before using this version.");
        }
    }

    /// <summary>Loads the authorized session snapshot, or <see langword="null"/> if absent.</summary>
    public async Task<MongoDBAgentSessionRecord?> GetAsync(
        string sessionId,
        AIAgent agent,
        JsonSerializerOptions? serializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        BsonDocument scope = Scope(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                try
                {
                    BsonDocument? document = await FindOneAsync(
                        IdentityFilter(scope),
                        token).ConfigureAwait(false);
                    return document is null
                        ? null
                        : await ToRecordAsync(
                            document,
                            new AIAgentSessionCodec(agent, serializerOptions),
                            token).ConfigureAwait(false);
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
                        "MongoDB Session Store retrieval failed.",
                        exception);
                }
            },
            _options.RetrievalTimeout,
            "MongoDB Session Store retrieval deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts a new authorized session snapshot. Fails if a session with the same identity already exists,
    /// unless the existing snapshot's content is identical to this call's (idempotent retry convergence).
    /// </summary>
    public async Task<MongoDBAgentSessionRecord> CreateAsync(
        string sessionId,
        AgentSession session,
        AIAgent agent,
        DateTimeOffset? expiresAt = null,
        JsonSerializerOptions? serializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agent);
        BsonDocument scope = Scope(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        var codec = new AIAgentSessionCodec(agent, serializerOptions);
        return await WithDeadlineAsync(
            async token =>
            {
                BsonBinaryData payload = await SerializePayloadAsync(codec, session, token)
                    .ConfigureAwait(false);
                DateTimeOffset now = _clock();
                DateTimeOffset? effectiveExpiresAt = expiresAt ?? DefaultExpiresAt(now);
                bool expiryIsDefaultDerived = expiresAt is null && effectiveExpiresAt is not null;
                var candidate = new BsonDocument
                {
                    { "_id", ScopedId(scope, sessionId) },
                    { "schema_version", SchemaVersion },
                    { "framework_version", FrameworkSerializationVersion },
                    { "version", 1L },
                    { "created_at", now.UtcDateTime },
                    { "updated_at", now.UtcDateTime },
                    {
                        "expires_at",
                        effectiveExpiresAt is { } expires
                            ? expires.UtcDateTime
                            : BsonNull.Value
                    },
                    { "session", payload },
                };
                candidate.AddRange(scope);
                try
                {
                    await _collection.InsertOneAsync(candidate, cancellationToken: token)
                        .ConfigureAwait(false);
                }
                catch (MongoException exception) when (IsDuplicateKey(exception))
                {
                    BsonDocument? existing = await FindOneAsync(IdentityFilter(scope), token)
                        .ConfigureAwait(false);
                    if (existing is not null && !HasCompatibleSchema(existing))
                    {
                        throw IncompatibleSchemaException();
                    }

                    if (existing is not null &&
                        ContentEquals(existing, payload, effectiveExpiresAt, expiryIsDefaultDerived, now))
                    {
                        // Converge on the persisted result unchanged: a retry never extends the expiry that the
                        // original, successful attempt already wrote.
                        return await ToRecordAsync(existing, codec, token).ConfigureAwait(false);
                    }

                    throw new MongoDBConcurrencyException(
                        "A session with the same authorized identity already exists with different content. " +
                        "Use SetAsync with the current version to update it.",
                        exception);
                }

                return await ToRecordAsync(candidate, codec, token).ConfigureAwait(false);
            },
            _options.PersistenceTimeout,
            "MongoDB Session Store persistence deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces (or, when <paramref name="expectedVersion"/> is <see langword="null"/>, unconditionally creates
    /// or replaces) the authorized session snapshot. When <paramref name="expectedVersion"/> is supplied, the
    /// write is an atomic compare-and-swap: it succeeds only if the stored version still matches, and a retried
    /// call whose stored result already reflects this exact content converges rather than conflicting.
    /// </summary>
    public async Task<MongoDBAgentSessionRecord> SetAsync(
        string sessionId,
        AgentSession session,
        AIAgent agent,
        string? expectedVersion = null,
        DateTimeOffset? expiresAt = null,
        JsonSerializerOptions? serializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agent);
        long? parsedExpectedVersion = ParseVersionOrNull(expectedVersion);
        BsonDocument scope = Scope(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        var codec = new AIAgentSessionCodec(agent, serializerOptions);
        return await WithDeadlineAsync(
            async token =>
            {
                BsonBinaryData payload = await SerializePayloadAsync(codec, session, token)
                    .ConfigureAwait(false);
                DateTimeOffset now = _clock();
                DateTimeOffset? effectiveExpiresAt = expiresAt ?? DefaultExpiresAt(now);
                bool expiryIsDefaultDerived = expiresAt is null && effectiveExpiresAt is not null;
                FilterDefinition<BsonDocument> filter = IdentityFilter(scope) &
                    Builders<BsonDocument>.Filter.Eq("schema_version", SchemaVersion) &
                    Builders<BsonDocument>.Filter.Eq("framework_version", FrameworkSerializationVersion);
                if (parsedExpectedVersion is { } expected)
                {
                    filter &= Builders<BsonDocument>.Filter.Eq("version", expected);
                }

                UpdateDefinition<BsonDocument> update = Builders<BsonDocument>.Update
                    .Set("session", payload)
                    .Set("updated_at", now.UtcDateTime)
                    .Set(
                        "expires_at",
                        effectiveExpiresAt is { } expires ? (BsonValue)expires.UtcDateTime : BsonNull.Value)
                    .Inc("version", 1L)
                    .SetOnInsert("schema_version", SchemaVersion)
                    .SetOnInsert("framework_version", FrameworkSerializationVersion)
                    .SetOnInsert("created_at", now.UtcDateTime)
                    .SetOnInsert("session_id", scope["session_id"])
                    .SetOnInsert("scope_discriminator", scope["scope_discriminator"])
                    .SetOnInsert("tenant_id", scope["tenant_id"])
                    .SetOnInsert("application_id", scope["application_id"])
                    .SetOnInsert("agent_id", scope["agent_id"])
                    .SetOnInsert("user_id", scope["user_id"]);
                bool isUpsert = parsedExpectedVersion is null;
                BsonDocument? result;
                try
                {
                    result = await _collection.FindOneAndUpdateAsync(
                        filter,
                        update,
                        new FindOneAndUpdateOptions<BsonDocument, BsonDocument>
                        {
                            IsUpsert = isUpsert,
                            ReturnDocument = ReturnDocument.After,
                        },
                        token).ConfigureAwait(false);
                }
                catch (MongoException exception) when (isUpsert && IsDuplicateKey(exception))
                {
                    // The schema/framework-version-scoped filter above never matches an incompatible existing
                    // document, so an unconditional (no expected version) upsert attempted to insert a new
                    // document at the same deterministic _id and collided with it. The failed insert did not
                    // mutate the existing document; detect and reject the incompatibility read-only rather than
                    // reinterpreting it.
                    BsonDocument? incompatible = await FindOneAsync(IdentityFilter(scope), token)
                        .ConfigureAwait(false);
                    if (incompatible is not null && !HasCompatibleSchema(incompatible))
                    {
                        throw IncompatibleSchemaException();
                    }

                    throw;
                }

                if (result is not null)
                {
                    return await ToRecordAsync(result, codec, token).ConfigureAwait(false);
                }

                // Only reachable when a specific expected version was required and no document matched (either
                // because none exists, because its version differs, or because its schema/framework markers are
                // incompatible and were therefore excluded by the filter above without being mutated).
                BsonDocument? existing = await FindOneAsync(IdentityFilter(scope), token)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    throw new MongoDBConcurrencyException(
                        "No session exists at the authorized identity for the expected version. " +
                        "Use CreateAsync, or SetAsync without an expected version, to create it.");
                }

                if (!HasCompatibleSchema(existing))
                {
                    throw IncompatibleSchemaException();
                }

                if (existing["version"].ToInt64() == parsedExpectedVersion!.Value + 1 &&
                    ContentEquals(existing, payload, effectiveExpiresAt, expiryIsDefaultDerived, now))
                {
                    // The exact write already succeeded on a prior, unacknowledged attempt: converge on the
                    // persisted result unchanged. Do not extend the expiry that write already committed.
                    return await ToRecordAsync(existing, codec, token).ConfigureAwait(false);
                }

                throw new MongoDBConcurrencyException(
                    $"Expected version '{expectedVersion}' does not match the stored version " +
                    $"'{existing["version"].ToInt64().ToString(CultureInfo.InvariantCulture)}'. " +
                    "Reload the current session and retry.");
            },
            _options.PersistenceTimeout,
            "MongoDB Session Store persistence deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the authorized session snapshot. Returns <see langword="false"/> when no matching snapshot
    /// exists (an idempotent no-op), and throws <see cref="MongoDBConcurrencyException"/> when
    /// <paramref name="expectedVersion"/> is supplied but a differently versioned snapshot exists.
    /// </summary>
    public async Task<bool> DeleteAsync(
        string sessionId,
        string? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        long? parsedExpectedVersion = ParseVersionOrNull(expectedVersion);
        BsonDocument scope = Scope(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                FilterDefinition<BsonDocument> filter = IdentityFilter(scope) &
                    Builders<BsonDocument>.Filter.Eq("schema_version", SchemaVersion) &
                    Builders<BsonDocument>.Filter.Eq("framework_version", FrameworkSerializationVersion);
                if (parsedExpectedVersion is { } expected)
                {
                    filter &= Builders<BsonDocument>.Filter.Eq("version", expected);
                }

                DeleteResult result = await _collection.DeleteOneAsync(filter, token)
                    .ConfigureAwait(false);
                if (!result.IsAcknowledged)
                {
                    throw new MongoDBPersistenceException(
                        "MongoDB Session Store delete was not acknowledged.");
                }

                if (result.DeletedCount > 0)
                {
                    return true;
                }

                // Nothing matched the schema/framework-scoped filter above: distinguish not-found from an
                // incompatible document (rejected read-only, without mutation, regardless of whether an expected
                // version was supplied) from a genuine compare-and-swap conflict.
                BsonDocument? existing = await FindOneAsync(IdentityFilter(scope), token)
                    .ConfigureAwait(false);
                if (existing is not null && !HasCompatibleSchema(existing))
                {
                    throw IncompatibleSchemaException();
                }

                if (parsedExpectedVersion is not null && existing is not null)
                {
                    throw new MongoDBConcurrencyException(
                        $"Expected version '{expectedVersion}' does not match the stored version " +
                        $"'{existing["version"].ToInt64().ToString(CultureInfo.InvariantCulture)}'. " +
                        "Reload the current session and retry the deletion.");
                }

                return false;
            },
            _options.PersistenceTimeout,
            "MongoDB Session Store persistence deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists authorized session summaries in ascending session-ID order, without deserializing session content
    /// (no <see cref="AIAgent"/> is required). Supports cleanup and administrative enumeration.
    /// </summary>
    public async Task<MongoDBAgentSessionPage> ListAsync(
        int limit,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10_000)
        {
            throw new MongoDBConfigurationException("limit must be between 1 and 10000.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                try
                {
                    FilterDefinition<BsonDocument> filter = ScopeFilter(IsolationScope()) & NotExpiredFilter();
                    if (!string.IsNullOrEmpty(continuationToken))
                    {
                        filter &= Builders<BsonDocument>.Filter.Gt("session_id", continuationToken);
                    }

                    var findOptions = new FindOptions<BsonDocument, BsonDocument>
                    {
                        Sort = Builders<BsonDocument>.Sort.Ascending("session_id"),
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

                    return new MongoDBAgentSessionPage
                    {
                        Items = documents.Select(ToSummary).ToArray(),
                        ContinuationToken = hasMore
                            ? documents[^1]["session_id"].AsString
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
                        "MongoDB Session Store list failed.",
                        exception);
                }
            },
            _options.RetrievalTimeout,
            "MongoDB Session Store retrieval deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Explicitly provisions the required regular lookup index and the optional TTL index.</summary>
    public async Task<IReadOnlyList<string>> EnsureIndexesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var models = new List<CreateIndexModel<BsonDocument>>
        {
            new(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("scope_discriminator")
                    .Ascending("session_id"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "session_scope_lookup",
                    Unique = true,
                }),
            new(
                Builders<BsonDocument>.IndexKeys.Ascending("expires_at"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "session_expiration_ttl",
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
                "MongoDB Session Store index provisioning failed.",
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
                "session_scope_lookup",
                ["scope_discriminator", "session_id"],
                expectedUnique: true,
                expectedPartial: BsonNull.Value);
            BsonDocument ttl = ValidateIndex(
                indexes,
                "session_expiration_ttl",
                ["expires_at"],
                expectedUnique: false,
                expectedPartial: new BsonDocument("expires_at", new BsonDocument("$type", "date")));
            if (!ttl.TryGetValue("expireAfterSeconds", out BsonValue seconds) ||
                seconds.IsBsonNull ||
                seconds.ToDouble() != 0)
            {
                throw new MongoDBIndexMismatchException(
                    "Regular index 'session_expiration_ttl' does not match the required Session Store definition.");
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
                "MongoDB Session Store index validation failed.",
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

    private BsonDocument IsolationScope()
    {
        var dimensions = new BsonDocument
        {
            { "tenant_id", _options.TenantId is null ? BsonNull.Value : _options.TenantId },
            { "application_id", _options.ApplicationId },
            { "agent_id", _options.AgentId },
            { "user_id", _options.UserId is null ? BsonNull.Value : _options.UserId },
        };
        return dimensions;
    }

    private BsonDocument Scope(string sessionId)
    {
        // sessionId is opaque and must not be trimmed: it is only required to be non-null and not
        // whitespace-only (enforced by RequireText). Leading/trailing whitespace is significant and must remain
        // distinct and independently reachable, e.g. " session-1" and "session-1 " are different sessions.
        MongoDBAgentSessionStoreOptions.RequireText(sessionId, nameof(sessionId));
        BsonDocument dimensions = IsolationScope();
        return new BsonDocument
        {
            {
                "scope_discriminator",
                CanonicalScopeDiscriminator(_options.TenantId, _options.ApplicationId, _options.AgentId, _options.UserId)
            },
            { "tenant_id", dimensions["tenant_id"] },
            { "application_id", dimensions["application_id"] },
            { "agent_id", dimensions["agent_id"] },
            { "user_id", dimensions["user_id"] },
            { "session_id", sessionId },
        };
    }

    private static FilterDefinition<BsonDocument> ScopeFilter(BsonDocument dimensions) =>
        Builders<BsonDocument>.Filter.Eq("tenant_id", dimensions["tenant_id"]) &
        Builders<BsonDocument>.Filter.Eq("application_id", dimensions["application_id"]) &
        Builders<BsonDocument>.Filter.Eq("agent_id", dimensions["agent_id"]) &
        Builders<BsonDocument>.Filter.Eq("user_id", dimensions["user_id"]);

    private static FilterDefinition<BsonDocument> IdentityFilter(BsonDocument scope) =>
        Builders<BsonDocument>.Filter.Eq("_id", ScopedId(scope, scope["session_id"].AsString)) &
        ScopeFilter(scope) &
        Builders<BsonDocument>.Filter.Eq("session_id", scope["session_id"]);

    /// <summary>
    /// A document is not expired when it has no expiration (<c>expires_at</c> is null) or its expiration is
    /// still in the future. Applied to <see cref="ListAsync"/> so administrative enumeration never surfaces a
    /// session that is logically expired but has not yet been reaped by the TTL index.
    /// </summary>
    private static FilterDefinition<BsonDocument> NotExpiredFilter() =>
        Builders<BsonDocument>.Filter.Eq("expires_at", BsonNull.Value) |
        Builders<BsonDocument>.Filter.Gt("expires_at", DateTime.UtcNow);

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

    private static async Task<BsonBinaryData> SerializePayloadAsync(
        IAgentSessionCodec codec,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        JsonElement element = await codec.SerializeAsync(session, cancellationToken).ConfigureAwait(false);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new MongoDBMappingException(
                "Agent Framework AgentSession serialization did not produce a JSON object; the session cannot " +
                "be stored losslessly.");
        }

        try
        {
            // The public serializer's UTF-8 JSON bytes are persisted verbatim as BSON Binary rather than parsed
            // into a BsonDocument: BsonDocument.Parse retypes JSON numeric literals through BSON's native numeric
            // types (int32/int64/double/decimal128) using heuristics, which is lossy for unknown numeric shapes
            // (large integers, trailing-zero decimals, etc.). Storing the exact bytes and reversing with
            // JsonDocument.Parse on read guarantees byte-for-byte round-tripping of unknown content.
            byte[] bytes = Encoding.UTF8.GetBytes(element.GetRawText());
            return new BsonBinaryData(bytes, BsonBinarySubType.Binary);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new MongoDBMappingException(
                "Agent Framework AgentSession could not be serialized losslessly.",
                exception);
        }
    }

    private static async Task<MongoDBAgentSessionRecord> ToRecordAsync(
        BsonDocument document,
        IAgentSessionCodec codec,
        CancellationToken cancellationToken)
    {
        ValidateSchemaVersion(document);
        JsonElement element = DeserializePayloadElement(document);
        AgentSession session = await codec.DeserializeAsync(element, cancellationToken).ConfigureAwait(false);
        return new MongoDBAgentSessionRecord
        {
            SessionId = document["session_id"].AsString,
            Session = session,
            Version = document["version"].ToInt64().ToString(CultureInfo.InvariantCulture),
            CreatedAt = new DateTimeOffset(document["created_at"].ToUniversalTime()),
            UpdatedAt = new DateTimeOffset(document["updated_at"].ToUniversalTime()),
            ExpiresAt = document.TryGetValue("expires_at", out BsonValue expires) && !expires.IsBsonNull
                ? new DateTimeOffset(expires.ToUniversalTime())
                : null,
        };
    }

    private static MongoDBAgentSessionSummary ToSummary(BsonDocument document)
    {
        ValidateSchemaVersion(document);
        return new MongoDBAgentSessionSummary
        {
            SessionId = document["session_id"].AsString,
            Version = document["version"].ToInt64().ToString(CultureInfo.InvariantCulture),
            CreatedAt = new DateTimeOffset(document["created_at"].ToUniversalTime()),
            UpdatedAt = new DateTimeOffset(document["updated_at"].ToUniversalTime()),
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

    /// <summary>
    /// Returns whether a stored document's <c>schema_version</c>/<c>framework_version</c> markers match the
    /// versions this build understands, without throwing. Used before any mutation so an incompatible document
    /// is detected and rejected read-only, distinct from a not-found or a genuine compare-and-swap conflict.
    /// </summary>
    private static bool HasCompatibleSchema(BsonDocument document) =>
        document.TryGetValue("schema_version", out BsonValue schema) &&
        schema.IsInt32 && schema.AsInt32 == SchemaVersion &&
        document.TryGetValue("framework_version", out BsonValue framework) &&
        framework.IsInt32 && framework.AsInt32 == FrameworkSerializationVersion;

    /// <summary>
    /// The exception thrown when a stored document exists at the authorized identity but its
    /// <c>schema_version</c>/<c>framework_version</c> markers are not supported by this build. Reused by every
    /// load and mutation path so this specific condition is always distinguishable from "not found" and from a
    /// genuine compare-and-swap version conflict, and is never silently reinterpreted or partially mutated.
    /// </summary>
    private static MongoDBMappingException IncompatibleSchemaException() =>
        new(
            "The stored session at this authorized identity was written with an unsupported schema_version or " +
            "framework_version for this build (expected schema_version " +
            SchemaVersion.ToString(CultureInfo.InvariantCulture) + " and framework_version " +
            FrameworkSerializationVersion.ToString(CultureInfo.InvariantCulture) +
            "). No read, update, or delete was attempted against it. Follow the manual remediation in " +
            "docs/development/persistence/dotnet-session-store-migration.md before retrying.");

    private static JsonElement DeserializePayloadElement(BsonDocument document)
    {
        if (!document.TryGetValue("session", out BsonValue payload) || payload.BsonType != BsonType.Binary)
        {
            throw new MongoDBMappingException(
                "Stored Session Store payload is invalid. Follow the manual remediation in " +
                "docs/development/persistence/dotnet-session-store-migration.md before retrying.");
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
                "Stored Session Store payload is incompatible. Follow the manual remediation in " +
                "docs/development/persistence/dotnet-session-store-migration.md before retrying.",
                exception);
        }
    }

    /// <summary>
    /// Compares stored envelope state against a candidate write for idempotent-retry convergence. The exact
    /// serialized session payload bytes must always match. The expiration comparison then depends on the
    /// candidate expiry's origin (see <see cref="ExpiresAtEquals"/>): an explicit caller-supplied
    /// <c>expiresAt</c> must match the stored value exactly (a retry that resends identical session content but
    /// a different explicit intended expiration is a genuine conflict, not a converging retry), while a
    /// default-derived candidate (the caller supplied no <c>expiresAt</c> and <see cref="MongoDBAgentSessionStoreOptions.DefaultExpiration"/>
    /// computed one from "now") converges whenever the stored expiration is still a compatible, still-future
    /// default expiration -- since a retry recomputes "now" later than the original attempt did, comparing exact
    /// timestamps in that case would spuriously conflict on every retry.
    /// </summary>
    private static bool ContentEquals(
        BsonDocument existing,
        BsonBinaryData candidatePayload,
        DateTimeOffset? candidateExpiresAt,
        bool candidateExpiryIsDefaultDerived,
        DateTimeOffset now) =>
        existing.TryGetValue("session", out BsonValue existingPayload) &&
        existingPayload.BsonType == BsonType.Binary &&
        existingPayload.AsBsonBinaryData.Bytes.AsSpan().SequenceEqual(candidatePayload.Bytes) &&
        ExpiresAtEquals(existing, candidateExpiresAt, candidateExpiryIsDefaultDerived, now);

    private static bool ExpiresAtEquals(
        BsonDocument existing,
        DateTimeOffset? candidateExpiresAt,
        bool candidateExpiryIsDefaultDerived,
        DateTimeOffset now)
    {
        bool existingHasExpiry = existing.TryGetValue("expires_at", out BsonValue expires) && !expires.IsBsonNull;

        if (candidateExpiryIsDefaultDerived)
        {
            // The candidate's expiry was freshly computed from "now" because the caller supplied no explicit
            // expiresAt. A retry of the same logical write recomputes "now" later than the original attempt
            // did, so its default-derived candidate will almost never equal the persisted timestamp exactly --
            // comparing them exactly would spuriously treat every retry as a conflict. Converge instead purely
            // on whether the existing document already carries a still-future expiration (consistent with this
            // store's default-expiration semantics), and never extend it: the retry returns the original,
            // already-persisted expiry unchanged rather than pushing it further into the future. An existing
            // document with no expiry, or one whose expiry has already passed, is not a compatible default-
            // expiration convergence target and is therefore a genuine conflict.
            return existingHasExpiry && expires.ToUniversalTime() > now.UtcDateTime;
        }

        if (!existingHasExpiry)
        {
            return candidateExpiresAt is null;
        }

        if (candidateExpiresAt is null)
        {
            return false;
        }

        DateTime existingUtc = expires.ToUniversalTime();
        DateTime candidateUtc = TruncateToMillisecond(candidateExpiresAt.Value.UtcDateTime);
        return existingUtc == candidateUtc;
    }

    private static DateTime TruncateToMillisecond(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Kind);

    private static long? ParseVersionOrNull(string? version)
    {
        if (version is null)
        {
            return null;
        }

        if (!long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ||
            parsed < 1)
        {
            throw new MongoDBConfigurationException(
                $"'{version}' is not a valid Session Store version token.");
        }

        return parsed;
    }

    private static bool IsDuplicateKey(MongoException exception) =>
        exception is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } ||
        exception is MongoCommandException { Code: 11000 or 11001 };

    private static string ScopedId(BsonDocument scope, string sessionId) =>
        Hash($"session|{scope["scope_discriminator"].AsString}|{sessionId}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string CanonicalScopeDiscriminator(
        string? tenantId,
        string applicationId,
        string agentId,
        string? userId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("dimensions");
            writer.WriteStartObject();
            writer.WriteString("agent_id", agentId);
            writer.WriteString("application_id", applicationId);
            if (tenantId is null)
            {
                writer.WriteNull("tenant_id");
            }
            else
            {
                writer.WriteString("tenant_id", tenantId);
            }

            if (userId is null)
            {
                writer.WriteNull("user_id");
            }
            else
            {
                writer.WriteString("user_id", userId);
            }

            writer.WriteEndObject();
            writer.WriteNumber("version", 1);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static BsonDocument ValidateIndex(
        IReadOnlyList<BsonDocument> indexes,
        string name,
        IReadOnlyList<string> expectedKeys,
        bool expectedUnique,
        BsonValue expectedPartial)
    {
        BsonDocument? index = indexes.FirstOrDefault(
            candidate => candidate.GetValue("name", "") == name);
        if (index is null)
        {
            throw new MongoDBIndexMissingException(
                $"Required regular index '{name}' is missing; run EnsureIndexesAsync.");
        }

        if (!index.TryGetValue("key", out BsonValue keys) ||
            !keys.IsBsonDocument ||
            !keys.AsBsonDocument.Names.SequenceEqual(expectedKeys, StringComparer.Ordinal) ||
            keys.AsBsonDocument.Values.Any(value => value.ToInt32() != 1) ||
            index.GetValue("unique", false).ToBoolean() != expectedUnique ||
            index.GetValue("partialFilterExpression", BsonNull.Value) != expectedPartial)
        {
            throw new MongoDBIndexMismatchException(
                $"Regular index '{name}' does not match the required Session Store definition.");
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
