namespace MongoDB.AgentFramework;

/// <summary>
/// Immutable authorization scope and TTL/deadline options for <see cref="MongoDBCheckpointStore"/>. One
/// instance scopes every operation to exactly one workflow definition (and, if configured, one tenant); the
/// <c>sessionId</c> parameter threaded through every <see cref="MongoDBCheckpointStore"/> method is the
/// workflow run/session partition within that scope.
/// </summary>
public sealed record MongoDBCheckpointStoreOptions
{
    /// <summary>Gets the optional tenant isolation identifier.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets the required workflow definition identifier.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>
    /// Gets the default TTL applied when a caller does not pass an explicit <c>expiresAt</c> to
    /// <see cref="MongoDBCheckpointStore.SaveCheckpointAsync"/> or when the framework's own
    /// <see cref="MongoDBCheckpointStore.CreateCheckpointAsync"/> hook is invoked (which accepts no expiry
    /// parameter at all). Checkpoints written without any expiration (neither this default nor an explicit
    /// value) never expire. Expiring a checkpoint that is a lineage parent of a still-live checkpoint leaves a
    /// lineage gap; see docs/spec/features/persistence.md.
    /// </summary>
    public TimeSpan? DefaultExpiration { get; init; }

    /// <summary>Gets the optional complete retrieval/list deadline.</summary>
    public TimeSpan? RetrievalTimeout { get; init; }

    /// <summary>Gets the optional complete save/delete deadline.</summary>
    public TimeSpan? PersistenceTimeout { get; init; }

    /// <summary>Validates configuration without contacting MongoDB.</summary>
    public void Validate()
    {
        RequireText(WorkflowId, nameof(WorkflowId));
        if (TenantId is not null)
        {
            RequireText(TenantId, nameof(TenantId));
        }

        ValidateDuration(DefaultExpiration, nameof(DefaultExpiration));
        ValidateDuration(RetrievalTimeout, nameof(RetrievalTimeout));
        ValidateDuration(PersistenceTimeout, nameof(PersistenceTimeout));
    }

    internal static string RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MongoDBConfigurationException($"{name} must not be empty.");
        }

        return value;
    }

    private static void ValidateDuration(TimeSpan? value, string name)
    {
        if (value is { } duration && duration <= TimeSpan.Zero)
        {
            throw new MongoDBConfigurationException($"{name} must be positive when configured.");
        }
    }
}
