namespace MongoDB.AgentFramework;

/// <summary>Immutable authorization scope for MongoDB semantic Memory.</summary>
public sealed class MongoDBMemoryScope
{
    /// <summary>Creates a scope with at least one durable application, agent, or user identity.</summary>
    public MongoDBMemoryScope(
        string? applicationId = null,
        string? agentId = null,
        string? userId = null,
        string? sessionId = null)
    {
        ApplicationId = Normalize(applicationId, nameof(applicationId));
        AgentId = Normalize(agentId, nameof(agentId));
        UserId = Normalize(userId, nameof(userId));
        SessionId = Normalize(sessionId, nameof(sessionId));
        if (ApplicationId is null && AgentId is null && UserId is null)
        {
            throw new MongoDBConfigurationException(
                "At least one of applicationId, agentId, or userId is required.");
        }
    }

    /// <summary>Gets the application identity.</summary>
    public string? ApplicationId { get; }

    /// <summary>Gets the agent identity.</summary>
    public string? AgentId { get; }

    /// <summary>Gets the user identity.</summary>
    public string? UserId { get; }

    /// <summary>Gets the optional session restriction.</summary>
    public string? SessionId { get; }

    /// <summary>Creates this scope with a different session restriction.</summary>
    public MongoDBMemoryScope WithSession(string? sessionId) =>
        new(ApplicationId, AgentId, UserId, sessionId);

    internal IReadOnlyDictionary<string, string> ToFields()
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(fields, "application_id", ApplicationId);
        Add(fields, "agent_id", AgentId);
        Add(fields, "user_id", UserId);
        Add(fields, "session_id", SessionId);
        return fields;
    }

    private static void Add(Dictionary<string, string> fields, string name, string? value)
    {
        if (value is not null)
        {
            fields.Add(name, value);
        }
    }

    private static string? Normalize(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MongoDBConfigurationException($"{name} must not be empty.");
        }

        return value;
    }
}
