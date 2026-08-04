using Microsoft.Extensions.AI;

namespace MongoDB.AgentFramework;

/// <summary>Immutable authorization, filtering, loading, and retention options.</summary>
public sealed record MongoDBChatHistoryProviderOptions
{
    /// <summary>Gets the optional tenant isolation identifier.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets the required application authorization identifier.</summary>
    public required string ApplicationId { get; init; }

    /// <summary>Gets the required agent authorization identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>Gets the only session this provider is authorized to access.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the maximum number of latest messages loaded.</summary>
    public int MaxMessages { get; init; } = 100;

    /// <summary>Gets the optional maximum age of loaded messages.</summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>Gets optional physical retention applied through a TTL index.</summary>
    public TimeSpan? Retention { get; init; }

    /// <summary>Gets the optional complete retrieval deadline.</summary>
    public TimeSpan? RetrievalTimeout { get; init; }

    /// <summary>Gets the optional complete persistence deadline.</summary>
    public TimeSpan? PersistenceTimeout { get; init; }

    /// <summary>Gets the base-provider filter applied to loaded history.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? ProvideOutputMessageFilter { get; init; }

    /// <summary>Gets the base-provider filter applied to request messages before storage.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputRequestMessageFilter { get; init; }

    /// <summary>Gets the base-provider filter applied to response messages before storage.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputResponseMessageFilter { get; init; }

    /// <summary>Validates configuration without contacting MongoDB.</summary>
    public void Validate()
    {
        RequireText(ApplicationId, nameof(ApplicationId));
        RequireText(AgentId, nameof(AgentId));
        RequireText(SessionId, nameof(SessionId));
        if (TenantId is not null)
        {
            RequireText(TenantId, nameof(TenantId));
        }

        if (MaxMessages is < 1 or > 10_000)
        {
            throw new MongoDBConfigurationException("MaxMessages must be between 1 and 10000.");
        }

        ValidateDuration(MaxAge, nameof(MaxAge));
        ValidateDuration(Retention, nameof(Retention));
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
