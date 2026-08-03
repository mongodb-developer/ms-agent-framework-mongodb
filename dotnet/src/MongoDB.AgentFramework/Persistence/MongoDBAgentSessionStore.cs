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
/// </remarks>
public sealed class MongoDBAgentSessionStore : IAsyncDisposable
{
    /// <summary>The stored MongoDB envelope schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The internal Agent Framework JSON envelope compatibility marker (not the NuGet package version).</summary>
    public const int FrameworkSerializationVersion = 1;

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly MongoDBAgentSessionStoreOptions _options;
    private readonly OwnedResource<IMongoClient>? _client;

    /// <summary>Creates a store over an injected collection, which remains caller-owned.</summary>
    public MongoDBAgentSessionStore(
        IMongoCollection<BsonDocument> collection,
        MongoDBAgentSessionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options with
        {
            TenantId = options.TenantId?.Trim(),
            ApplicationId = options.ApplicationId.Trim(),
            AgentId = options.AgentId.Trim(),
            UserId = options.UserId?.Trim(),
        };
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
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
        : this(
            MongoClientFactory.FromConnectionString(connectionString),
            databaseName,
            collectionName,
            options)
    {
    }

    private MongoDBAgentSessionStore(
        OwnedResource<IMongoClient> client,
        string databaseName,
        string collectionName,
        MongoDBAgentSessionStoreOptions options)
        : this(client.Value, databaseName, collectionName, options)
    {
        _client = client;
    }

    /// <summary>Gets whether this store owns its MongoDB client.</summary>
    public bool OwnsClient => _client?.OwnsValue is true;

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
                BsonDocument payload = await SerializePayloadAsync(codec, session, token)
                    .ConfigureAwait(false);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset? effectiveExpiresAt = expiresAt ?? DefaultExpiresAt(now);
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
                    if (existing is not null && ContentEquals(existing, payload))
                    {
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
                BsonDocument payload = await SerializePayloadAsync(codec, session, token)
                    .ConfigureAwait(false);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset? effectiveExpiresAt = expiresAt ?? DefaultExpiresAt(now);
                FilterDefinition<BsonDocument> filter = IdentityFilter(scope);
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
                BsonDocument? result = await _collection.FindOneAndUpdateAsync(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<BsonDocument, BsonDocument>
                    {
                        IsUpsert = parsedExpectedVersion is null,
                        ReturnDocument = ReturnDocument.After,
                    },
                    token).ConfigureAwait(false);
                if (result is not null)
                {
                    return await ToRecordAsync(result, codec, token).ConfigureAwait(false);
                }

                // Only reachable when a specific expected version was required and no document matched it.
                BsonDocument? existing = await FindOneAsync(IdentityFilter(scope), token)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    throw new MongoDBConcurrencyException(
                        "No session exists at the authorized identity for the expected version. " +
                        "Use CreateAsync, or SetAsync without an expected version, to create it.");
                }

                if (existing["version"].ToInt64() == parsedExpectedVersion!.Value + 1 &&
                    ContentEquals(existing, payload))
                {
                    // The exact write already succeeded on a prior, unacknowledged attempt: converge.
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
                FilterDefinition<BsonDocument> filter = IdentityFilter(scope);
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

                if (parsedExpectedVersion is not null)
                {
                    BsonDocument? existing = await FindOneAsync(IdentityFilter(scope), token)
                        .ConfigureAwait(false);
                    if (existing is not null)
                    {
                        throw new MongoDBConcurrencyException(
                            $"Expected version '{expectedVersion}' does not match the stored version " +
                            $"'{existing["version"].ToInt64().ToString(CultureInfo.InvariantCulture)}'. " +
                            "Reload the current session and retry the deletion.");
                    }
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
                    FilterDefinition<BsonDocument> filter = ScopeFilter(IsolationScope());
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
            { "session_id", sessionId.Trim() },
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

    private static async Task<BsonDocument> SerializePayloadAsync(
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
            return BsonDocument.Parse(element.GetRawText());
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
        if (!document.TryGetValue("schema_version", out BsonValue schema) ||
            !schema.IsInt32 ||
            schema.AsInt32 != SchemaVersion)
        {
            throw new MongoDBMappingException(
                "Unsupported Session Store schema version; run a supported migration before loading this " +
                "session.");
        }

        if (!document.TryGetValue("framework_version", out BsonValue framework) ||
            !framework.IsInt32 ||
            framework.AsInt32 != FrameworkSerializationVersion)
        {
            throw new MongoDBMappingException(
                "Unsupported Session Store framework serialization version; run a supported migration before " +
                "loading this session.");
        }
    }

    private static JsonElement DeserializePayloadElement(BsonDocument document)
    {
        if (!document.TryGetValue("session", out BsonValue payload) || !payload.IsBsonDocument)
        {
            throw new MongoDBMappingException(
                "Stored Session Store payload is invalid; migration is required.");
        }

        try
        {
            string json = payload.AsBsonDocument.ToJson(
                new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson });
            using JsonDocument parsed = JsonDocument.Parse(json);
            return parsed.RootElement.Clone();
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new MongoDBMappingException(
                "Stored Session Store payload is incompatible; run a supported migration.",
                exception);
        }
    }

    private static bool ContentEquals(BsonDocument existing, BsonDocument candidatePayload) =>
        existing.TryGetValue("session", out BsonValue existingPayload) &&
        existingPayload.IsBsonDocument &&
        existingPayload.AsBsonDocument.Equals(candidatePayload);

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
