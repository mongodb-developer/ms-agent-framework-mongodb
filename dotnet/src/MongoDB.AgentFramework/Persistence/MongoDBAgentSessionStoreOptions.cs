namespace MongoDB.AgentFramework;

/// <summary>Immutable authorization scope and TTL/deadline options for <see cref="MongoDBAgentSessionStore"/>.</summary>
public sealed record MongoDBAgentSessionStoreOptions
{
    /// <summary>Gets the optional tenant isolation identifier.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets the required application authorization identifier.</summary>
    public required string ApplicationId { get; init; }

    /// <summary>Gets the required agent authorization identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>Gets the optional user isolation identifier.</summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Gets the default TTL applied when a caller does not pass an explicit <c>expiresAt</c> to
    /// <see cref="MongoDBAgentSessionStore.CreateAsync"/> or <see cref="MongoDBAgentSessionStore.SetAsync"/>.
    /// Sessions written without any expiration (neither this default nor an explicit value) never expire.
    /// </summary>
    public TimeSpan? DefaultExpiration { get; init; }

    /// <summary>Gets the optional complete retrieval/list deadline.</summary>
    public TimeSpan? RetrievalTimeout { get; init; }

    /// <summary>Gets the optional complete create/set/delete deadline.</summary>
    public TimeSpan? PersistenceTimeout { get; init; }

    /// <summary>Validates configuration without contacting MongoDB.</summary>
    public void Validate()
    {
        RequireText(ApplicationId, nameof(ApplicationId));
        RequireText(AgentId, nameof(AgentId));
        if (TenantId is not null)
        {
            RequireText(TenantId, nameof(TenantId));
        }

        if (UserId is not null)
        {
            RequireText(UserId, nameof(UserId));
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
