using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.AgentFramework.Internal;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MongoDB.AgentFramework;

/// <summary>Persists and replays exact ordered Agent Framework chat history.</summary>
public sealed class MongoDBChatHistoryProvider : ChatHistoryProvider, IAsyncDisposable
{
    /// <summary>The stored MongoDB envelope schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The public Agent Framework JSON serialization version.</summary>
    public const int FrameworkSerializationVersion = 1;

    private const int RetryStateVersion = 1;
    private static readonly IReadOnlyList<string> ProviderStateKeys =
        ["mongodb_history_pending_batches"];
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly MongoDBChatHistoryProviderOptions _options;
    private readonly OwnedResource<IMongoClient>? _client;
    private readonly object _retryLock = new();
    private readonly RetryState _directRetryState = new();
    private readonly HashSet<string> _activeRetryAttempts = [];

    /// <summary>Creates a provider over an injected collection, which remains caller-owned.</summary>
    public MongoDBChatHistoryProvider(
        IMongoCollection<BsonDocument> collection,
        MongoDBChatHistoryProviderOptions options)
        : base(
            options?.ProvideOutputMessageFilter,
            options?.StoreInputRequestMessageFilter,
            options?.StoreInputResponseMessageFilter)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options with
        {
            TenantId = options.TenantId?.Trim(),
            ApplicationId = options.ApplicationId.Trim(),
            AgentId = options.AgentId.Trim(),
            SessionId = options.SessionId.Trim(),
        };
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    /// <summary>Creates a provider over an injected database, which remains caller-owned.</summary>
    public MongoDBChatHistoryProvider(
        IMongoDatabase database,
        string collectionName,
        MongoDBChatHistoryProviderOptions options)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database))).GetCollection<BsonDocument>(
                MongoDBChatHistoryProviderOptions.RequireText(collectionName, nameof(collectionName))),
            options)
    {
    }

    /// <summary>Creates a provider over an injected client, which remains caller-owned.</summary>
    public MongoDBChatHistoryProvider(
        IMongoClient client,
        string databaseName,
        string collectionName,
        MongoDBChatHistoryProviderOptions options)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client))).GetDatabase(
                MongoDBChatHistoryProviderOptions.RequireText(databaseName, nameof(databaseName))),
            collectionName,
            options)
    {
    }

    /// <summary>Creates a provider-owned client from a connection string.</summary>
    public MongoDBChatHistoryProvider(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBChatHistoryProviderOptions options)
        : this(
            MongoClientFactory.FromConnectionString(connectionString),
            databaseName,
            collectionName,
            options)
    {
    }

    private MongoDBChatHistoryProvider(
        OwnedResource<IMongoClient> client,
        string databaseName,
        string collectionName,
        MongoDBChatHistoryProviderOptions options)
        : this(client.Value, databaseName, collectionName, options)
    {
        _client = client;
    }

    /// <summary>Gets whether this provider owns its MongoDB client.</summary>
    public bool OwnsClient => _client?.OwnsValue is true;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => ProviderStateKeys;

    /// <summary>Loads the latest authorized messages in chronological order.</summary>
    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        BsonDocument scope = SessionScope(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                try
                {
                    FilterDefinition<BsonDocument> filter = ScopeFilter(scope) &
                        Builders<BsonDocument>.Filter.Eq("_kind", "message");
                    if (_options.MaxAge is { } maxAge)
                    {
                        filter &= Builders<BsonDocument>.Filter.Gte(
                            "created_at",
                            DateTime.UtcNow - maxAge);
                    }

                    var options = new FindOptions<BsonDocument, BsonDocument>
                    {
                        Sort = Builders<BsonDocument>.Sort.Descending("sequence"),
                        Limit = _options.MaxMessages,
                    };
                    using IAsyncCursor<BsonDocument> cursor = await _collection.FindAsync(
                        filter,
                        options,
                        token).ConfigureAwait(false);
                    var documents = new List<BsonDocument>();
                    while (await cursor.MoveNextAsync(token).ConfigureAwait(false))
                    {
                        documents.AddRange(cursor.Current);
                    }

                    documents.Reverse();
                    return (IReadOnlyList<ChatMessage>)documents.Select(DeserializeMessage).ToArray();
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
                        "MongoDB History retrieval failed.",
                        exception);
                }
            },
            _options.RetrievalTimeout,
            "MongoDB History retrieval deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends an exact idempotent message batch to the authorized session.</summary>
    public Task SaveMessagesAsync(
        string sessionId,
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        SaveMessagesCoreAsync(sessionId, messages, sessionState: null, cancellationToken);

    private async Task SaveMessagesCoreAsync(
        string sessionId,
        IEnumerable<ChatMessage> messages,
        AgentSessionStateBag? sessionState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        BsonDocument scope = SessionScope(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        ChatMessage[] batch = messages.ToArray();
        if (batch.Length == 0)
        {
            return;
        }

        RetryAttempt? retryAttempt = null;
        try
        {
            await WithDeadlineAsync(
                async token =>
                {
                    var prepared = new List<PreparedMessage>(batch.Length);
                    for (int ordinal = 0; ordinal < batch.Length; ordinal++)
                    {
                        ChatMessage message = batch[ordinal];
                        prepared.Add(
                            new PreparedMessage(
                                message,
                                SerializeMessage(message),
                                ordinal,
                                !string.IsNullOrWhiteSpace(message.MessageId)));
                    }

                    if (prepared.Any(static item => !item.HasFrameworkId))
                    {
                        retryAttempt = BeginRetryAttempt(
                            BatchFingerprint(scope, prepared),
                            sessionState);
                    }

                    foreach (PreparedMessage item in prepared)
                    {
                        if (item.HasFrameworkId)
                        {
                            continue;
                        }

                        string key = item.Ordinal.ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                        if (!retryAttempt!.Ids.TryGetValue(key, out string? fallbackId))
                        {
                            fallbackId = Guid.NewGuid().ToString();
                            retryAttempt.Ids.Add(key, fallbackId);
                        }

                        item.Payload["messageId"] = fallbackId;
                    }

                    if (retryAttempt is not null)
                    {
                        PersistRetryAttempt(retryAttempt, sessionState);
                    }

                    var pending = new List<(
                        string Id,
                        string MessageId,
                        ChatMessage Message,
                        BsonDocument Payload)>();
                    foreach (PreparedMessage item in prepared)
                    {
                        string messageId = item.HasFrameworkId
                            ? item.Message.MessageId!
                            : retryAttempt!.Ids[item.Ordinal.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)];
                        string documentId = ScopedId(scope, messageId);
                        BsonDocument? existing = await FindOneAsync(
                            Builders<BsonDocument>.Filter.Eq("_id", documentId),
                            token).ConfigureAwait(false);
                        if (existing is not null)
                        {
                            ValidateDuplicate(existing, messageId, item.Payload);
                            continue;
                        }

                        pending.Add((documentId, messageId, item.Message, item.Payload));
                    }

                    if (pending.Count == 0)
                    {
                        return;
                    }

                    long firstSequence = await AllocateSequenceAsync(
                        scope,
                        pending.Count,
                        token).ConfigureAwait(false);
                    for (int offset = 0; offset < pending.Count; offset++)
                    {
                        var item = pending[offset];
                        DateTime now = DateTime.UtcNow;
                        var document = new BsonDocument
                        {
                            { "_id", item.Id },
                            { "_kind", "message" },
                            { "schema_version", SchemaVersion },
                            { "framework_version", FrameworkSerializationVersion },
                            { "sequence", firstSequence + offset },
                            { "message_id", item.MessageId },
                            { "role", item.Message.Role.Value },
                            { "created_at", now },
                            { "message", item.Payload },
                        };
                        document.AddRange(scope);
                        if (_options.Retention is { } retention)
                        {
                            document["expires_at"] = now + retention;
                        }

                        try
                        {
                            await _collection.InsertOneAsync(
                                document,
                                cancellationToken: token).ConfigureAwait(false);
                        }
                        catch (MongoException exception) when (IsDuplicateKey(exception))
                        {
                            BsonDocument? existing = await FindOneAsync(
                                ScopeFilter(scope) &
                                    Builders<BsonDocument>.Filter.Eq("_kind", "message") &
                                    Builders<BsonDocument>.Filter.Eq(
                                        "message_id",
                                        item.MessageId),
                                token).ConfigureAwait(false);
                            if (existing is null)
                            {
                                throw;
                            }

                            ValidateDuplicate(existing, item.MessageId, item.Payload);
                        }
                    }
                },
                _options.PersistenceTimeout,
                "MongoDB History persistence deadline exceeded.",
                cancellationToken).ConfigureAwait(false);
            FinishRetryAttempt(retryAttempt, sessionState, retryableFailure: false);
        }
        catch (OperationCanceledException)
        {
            FinishRetryAttempt(retryAttempt, sessionState, retryableFailure: true);
            throw;
        }
        catch (MongoDBTimeoutException)
        {
            FinishRetryAttempt(retryAttempt, sessionState, retryableFailure: true);
            throw;
        }
        catch (MongoException exception)
        {
            FinishRetryAttempt(retryAttempt, sessionState, retryableFailure: true);
            throw new MongoDBPersistenceException(
                "MongoDB History persistence failed.",
                exception);
        }
        catch
        {
            FinishRetryAttempt(retryAttempt, sessionState, retryableFailure: false);
            throw;
        }
    }

    /// <summary>Clears only the authorized session and resets its sequence allocator.</summary>
    public async Task<long> ClearMessagesAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        BsonDocument scope = SessionScope(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return await WithDeadlineAsync(
            async token =>
            {
                try
                {
                    DeleteResult result = await _collection.DeleteManyAsync(
                        ScopeFilter(scope) & Builders<BsonDocument>.Filter.Eq("_kind", "message"),
                        token).ConfigureAwait(false);
                    await _collection.DeleteOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", CounterId(scope)) &
                            Builders<BsonDocument>.Filter.Eq("_kind", "sequence") &
                            ScopeFilter(scope),
                        token).ConfigureAwait(false);
                    if (!result.IsAcknowledged)
                    {
                        throw new MongoDBPersistenceException(
                            "MongoDB History clear was not acknowledged.");
                    }

                    return result.DeletedCount;
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
                        "MongoDB History persistence failed.",
                        exception);
                }
            },
            _options.PersistenceTimeout,
            "MongoDB History persistence deadline exceeded.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Explicitly provisions required regular and optional TTL indexes.</summary>
    public async Task<IReadOnlyList<string>> EnsureIndexesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scopeKeys = new BsonDocument
        {
            { "tenant_id", 1 },
            { "application_id", 1 },
            { "agent_id", 1 },
            { "session_id", 1 },
        };
        var partial = new BsonDocument("_kind", "message");
        var models = new List<CreateIndexModel<BsonDocument>>
        {
            new(
                new BsonDocumentIndexKeysDefinition<BsonDocument>(
                    new BsonDocument(scopeKeys).Add("message_id", 1)),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "history_scoped_message_unique",
                    Unique = true,
                    PartialFilterExpression = partial,
                }),
            new(
                new BsonDocumentIndexKeysDefinition<BsonDocument>(
                    new BsonDocument(scopeKeys).Add("sequence", 1)),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "history_scoped_sequence",
                    Unique = true,
                    PartialFilterExpression = partial,
                }),
        };
        if (_options.Retention is not null)
        {
            models.Add(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("expires_at"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = "history_expiration_ttl",
                        ExpireAfter = TimeSpan.Zero,
                        PartialFilterExpression = partial,
                    }));
        }

        try
        {
            return (await _collection.Indexes.CreateManyAsync(
                models,
                cancellationToken).ConfigureAwait(false)).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw new MongoDBPersistenceException(
                "MongoDB History index provisioning failed.",
                exception);
        }
    }

    /// <summary>Validates required regular indexes without mutating MongoDB.</summary>
    public async Task ValidateIndexesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await _collection.Indexes.ListAsync(
                cancellationToken).ConfigureAwait(false);
            var indexes = new List<BsonDocument>();
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                indexes.AddRange(cursor.Current);
            }

            string[] scopeKeys = ["tenant_id", "application_id", "agent_id", "session_id"];
            ValidateIndex(
                indexes,
                "history_scoped_message_unique",
                [.. scopeKeys, "message_id"],
                requireUnique: true);
            ValidateIndex(
                indexes,
                "history_scoped_sequence",
                [.. scopeKeys, "sequence"],
                requireUnique: true);
            if (_options.Retention is not null)
            {
                BsonDocument ttl = ValidateIndex(
                    indexes,
                    "history_expiration_ttl",
                    ["expires_at"],
                    requireUnique: false);
                if (!ttl.TryGetValue("expireAfterSeconds", out BsonValue seconds) ||
                    seconds.IsBsonNull ||
                    seconds.ToDouble() != 0)
                {
                    throw new MongoDBIndexMismatchException(
                        "Regular index 'history_expiration_ttl' does not match the required History definition.");
                }
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
                "MongoDB History index validation failed.",
                exception);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken) =>
        await GetMessagesAsync(_options.SessionId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        await SaveMessagesCoreAsync(
            _options.SessionId,
            context.RequestMessages.Concat(context.ResponseMessages ?? []),
            context.Session?.StateBag,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private BsonDocument SessionScope(string sessionId)
    {
        if (!string.Equals(sessionId?.Trim(), _options.SessionId, StringComparison.Ordinal))
        {
            throw new MongoDBConfigurationException(
                "The requested SessionId does not match this provider's authorized session.");
        }

        var scope = new BsonDocument
        {
            { "application_id", _options.ApplicationId },
            { "agent_id", _options.AgentId },
            { "session_id", _options.SessionId },
        };
        if (_options.TenantId is not null)
        {
            scope.InsertAt(0, new BsonElement("tenant_id", _options.TenantId));
        }

        return scope;
    }

    private static FilterDefinition<BsonDocument> ScopeFilter(BsonDocument scope) =>
        new BsonDocumentFilterDefinition<BsonDocument>(scope);

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

    private async Task<long> AllocateSequenceAsync(
        BsonDocument scope,
        int count,
        CancellationToken cancellationToken)
    {
        FilterDefinition<BsonDocument> filter =
            Builders<BsonDocument>.Filter.Eq("_id", CounterId(scope)) &
            Builders<BsonDocument>.Filter.Eq("_kind", "sequence") &
            ScopeFilter(scope);
        UpdateDefinition<BsonDocument> update = Builders<BsonDocument>.Update
            .Inc("sequence", count)
            .SetOnInsert("schema_version", SchemaVersion)
            .SetOnInsert("framework_version", FrameworkSerializationVersion);
        BsonDocument? counter = await _collection.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<BsonDocument, BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken).ConfigureAwait(false);
        if (counter is null || !counter.TryGetValue("sequence", out BsonValue sequence))
        {
            throw new MongoDBPersistenceException(
                "MongoDB History sequence allocation returned no value.");
        }

        return sequence.ToInt64() - count + 1;
    }

    private static BsonDocument SerializeMessage(ChatMessage message)
    {
        try
        {
            string json = JsonSerializer.Serialize(
                message,
                AgentAbstractionsJsonUtilities.DefaultOptions);
            return BsonDocument.Parse(json);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new MongoDBMappingException(
                "Agent Framework ChatMessage could not be serialized losslessly.",
                exception);
        }
    }

    private static ChatMessage DeserializeMessage(BsonDocument document)
    {
        if (!document.TryGetValue("schema_version", out BsonValue schema) ||
            !schema.IsInt32 ||
            schema.AsInt32 != SchemaVersion)
        {
            throw new MongoDBMappingException(
                $"Unsupported History schema version; run a supported migration before replay.");
        }

        if (!document.TryGetValue("framework_version", out BsonValue framework) ||
            !framework.IsInt32 ||
            framework.AsInt32 != FrameworkSerializationVersion)
        {
            throw new MongoDBMappingException(
                "Unsupported framework serialization version; run a supported migration before replay.");
        }

        if (!document.TryGetValue("message", out BsonValue payload) ||
            !payload.IsBsonDocument)
        {
            throw new MongoDBMappingException(
                "Stored History message payload is invalid; migration is required.");
        }

        try
        {
            string json = payload.AsBsonDocument.ToJson(
                new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson });
            return JsonSerializer.Deserialize<ChatMessage>(
                    json,
                    AgentAbstractionsJsonUtilities.DefaultOptions) ??
                throw new JsonException("The framework serializer returned null.");
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new MongoDBMappingException(
                "Stored History payload is incompatible; run a supported migration.",
                exception);
        }
    }

    private static string BatchFingerprint(
        BsonDocument scope,
        IEnumerable<PreparedMessage> messages)
    {
        var value = new BsonDocument
        {
            { "scope", scope },
            {
                "messages",
                new BsonArray(messages.Select(static item => item.Payload))
            },
        };
        return Hash(value.ToJson());
    }

    private static string ScopedId(BsonDocument scope, string messageId) =>
        Hash($"message|{scope.ToJson()}|{messageId}");

    private static string CounterId(BsonDocument scope) =>
        $"history-sequence:{Hash(scope.ToJson())}";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private RetryAttempt BeginRetryAttempt(
        string fingerprint,
        AgentSessionStateBag? sessionState)
    {
        lock (_retryLock)
        {
            RetryState state = LoadRetryState(sessionState);
            NormalizeRetryState(state);
            if (!state.Batches!.TryGetValue(fingerprint, out RetryBatch? batch))
            {
                batch = new();
                state.Batches.Add(fingerprint, batch);
            }

            Dictionary<string, string> ids = batch.Failed!.Count == 0
                ? []
                : batch.Failed[0];
            if (batch.Failed.Count > 0)
            {
                batch.Failed.RemoveAt(0);
            }

            string attemptId = Guid.NewGuid().ToString();
            batch.InFlight!.Add(attemptId, ids);
            _activeRetryAttempts.Add(attemptId);
            return new RetryAttempt(fingerprint, attemptId, ids, state);
        }
    }

    private void PersistRetryAttempt(
        RetryAttempt attempt,
        AgentSessionStateBag? sessionState)
    {
        lock (_retryLock)
        {
            ValidateIdMap(attempt.Ids, "in-flight attempt");
            SaveRetryState(sessionState, attempt.State);
        }
    }

    private void FinishRetryAttempt(
        RetryAttempt? attempt,
        AgentSessionStateBag? sessionState,
        bool retryableFailure)
    {
        if (attempt is null)
        {
            return;
        }

        lock (_retryLock)
        {
            RetryState state = LoadRetryState(sessionState);
            NormalizeRetryState(state);
            _activeRetryAttempts.Remove(attempt.AttemptId);
            if (!state.Batches!.TryGetValue(attempt.Fingerprint, out RetryBatch? batch))
            {
                return;
            }

            batch.InFlight!.Remove(attempt.AttemptId);
            if (retryableFailure)
            {
                batch.Failed!.Add(attempt.Ids);
            }

            if (batch.Failed!.Count == 0 && batch.InFlight.Count == 0)
            {
                state.Batches.Remove(attempt.Fingerprint);
            }

            SaveRetryState(sessionState, state);
        }
    }

    private RetryState LoadRetryState(AgentSessionStateBag? sessionState)
    {
        if (sessionState is null)
        {
            return _directRetryState;
        }

        try
        {
            if (!sessionState.TryGetValue(
                    ProviderStateKeys[0],
                    out RetryState? state))
            {
                return new();
            }

            ValidateRetryState(state);
            return state!;
        }
        catch (MongoDBConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw InvalidRetryState(
                "the stored value cannot be deserialized",
                exception);
        }
    }

    private void NormalizeRetryState(RetryState state)
    {
        ValidateRetryState(state);
        foreach (RetryBatch batch in state.Batches!.Values)
        {
            foreach ((string attemptId, Dictionary<string, string> ids) in
                     batch.InFlight!.ToArray())
            {
                if (!_activeRetryAttempts.Contains(attemptId))
                {
                    batch.Failed!.Add(ids);
                    batch.InFlight!.Remove(attemptId);
                }
            }
        }
    }

    private static void ValidateRetryState(RetryState? state)
    {
        if (state is null || state.Version != RetryStateVersion || state.Batches is null)
        {
            throw InvalidRetryState(
                "the version is unsupported or required fields are missing");
        }

        foreach ((string fingerprint, RetryBatch? batch) in state.Batches)
        {
            if (string.IsNullOrWhiteSpace(fingerprint) ||
                batch?.Failed is null ||
                batch.InFlight is null)
            {
                throw InvalidRetryState("a batch has an invalid shape");
            }

            foreach (Dictionary<string, string>? ids in batch.Failed)
            {
                ValidateIdMap(ids, "failed attempt");
            }

            foreach ((string attemptId, Dictionary<string, string>? ids) in batch.InFlight)
            {
                if (string.IsNullOrWhiteSpace(attemptId))
                {
                    throw InvalidRetryState("an in-flight attempt ID is empty");
                }

                ValidateIdMap(ids, "in-flight attempt");
            }
        }
    }

    private static void ValidateIdMap(
        Dictionary<string, string>? ids,
        string location)
    {
        if (ids is null ||
            ids.Count == 0 ||
            ids.Any(static pair =>
                string.IsNullOrWhiteSpace(pair.Key) ||
                string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw InvalidRetryState($"{location} contains invalid fallback IDs");
        }
    }

    private static void SaveRetryState(
        AgentSessionStateBag? sessionState,
        RetryState state)
    {
        if (sessionState is null)
        {
            return;
        }

        if (state.Batches!.Count == 0)
        {
            sessionState.TryRemoveValue(ProviderStateKeys[0]);
        }
        else
        {
            sessionState.SetValue(ProviderStateKeys[0], state);
        }
    }

    private static MongoDBConfigurationException InvalidRetryState(
        string detail,
        Exception? innerException = null)
    {
        const string guidance =
            "MongoDB History provider session retry state is invalid and cannot be migrated. " +
            "Migration guidance: clear 'mongodb_history_pending_batches' or restore a supported state version.";
        return innerException is null
            ? new MongoDBConfigurationException($"{guidance} Detail: {detail}.")
            : new MongoDBConfigurationException(
                $"{guidance} Detail: {detail}.",
                innerException);
    }

    private static bool IsDuplicateKey(MongoException exception) =>
        exception is MongoWriteException
        {
            WriteError.Category: ServerErrorCategory.DuplicateKey,
        } ||
        exception is MongoCommandException { Code: 11000 or 11001 };

    private static void ValidateDuplicate(
        BsonDocument existing,
        string messageId,
        BsonDocument payload)
    {
        if (existing.GetValue("schema_version", BsonNull.Value) != SchemaVersion ||
            existing.GetValue("framework_version", BsonNull.Value) != FrameworkSerializationVersion ||
            existing.GetValue("message_id", BsonNull.Value) != messageId ||
            existing.GetValue("message", BsonNull.Value) != payload)
        {
            throw new MongoDBPersistenceException(
                "A duplicate History message identity contains incompatible stored data.");
        }
    }

    private static BsonDocument ValidateIndex(
        IEnumerable<BsonDocument> indexes,
        string name,
        IReadOnlyList<string> expectedKeys,
        bool requireUnique)
    {
        BsonDocument? index = indexes.FirstOrDefault(
            value => value.GetValue("name", "").AsString == name);
        if (index is null)
        {
            throw new MongoDBIndexMissingException(
                $"Regular index '{name}' does not exist; create it explicitly.");
        }

        if (!index.TryGetValue("key", out BsonValue keys) ||
            !keys.IsBsonDocument ||
            !keys.AsBsonDocument.Names.SequenceEqual(expectedKeys, StringComparer.Ordinal) ||
            keys.AsBsonDocument.Values.Any(value => value.ToInt32() != 1) ||
            (requireUnique && !index.GetValue("unique", false).ToBoolean()))
        {
            throw new MongoDBIndexMismatchException(
                $"Regular index '{name}' does not match the required History definition.");
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

    private static async Task WithDeadlineAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan? timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        await WithDeadlineAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            timeout,
            timeoutMessage,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record PreparedMessage(
        ChatMessage Message,
        BsonDocument Payload,
        int Ordinal,
        bool HasFrameworkId);

    private sealed record RetryAttempt(
        string Fingerprint,
        string AttemptId,
        Dictionary<string, string> Ids,
        RetryState State);

    private sealed class RetryState
    {
        public int Version { get; set; } = RetryStateVersion;

        public Dictionary<string, RetryBatch>? Batches { get; set; } = [];
    }

    private sealed class RetryBatch
    {
        public List<Dictionary<string, string>>? Failed { get; set; } = [];

        public Dictionary<string, Dictionary<string, string>>? InFlight { get; set; } = [];
    }
}
