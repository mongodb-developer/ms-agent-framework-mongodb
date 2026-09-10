using Microsoft.Agents.AI;

namespace MongoDB.AgentFramework;

/// <summary>A complete, deserialized authorized agent session snapshot and its optimistic concurrency metadata.</summary>
public sealed record MongoDBAgentSessionRecord
{
    /// <summary>Gets the authorized opaque session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the deserialized framework agent session.</summary>
    public required AgentSession Session { get; init; }

    /// <summary>
    /// Gets the compare-and-swap version token. Pass this value back as <c>expectedVersion</c> to
    /// <see cref="MongoDBAgentSessionStore.SetAsync"/> or <see cref="MongoDBAgentSessionStore.DeleteAsync"/> to
    /// guard against a lost update.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>Gets the UTC creation timestamp of the first stored snapshot for this session.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the UTC timestamp of the most recent stored snapshot for this session.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets the optional UTC expiration applied through the TTL index.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Metadata-only summary of a stored authorized session, returned by <see cref="MongoDBAgentSessionStore.ListAsync"/>.</summary>
public sealed record MongoDBAgentSessionSummary
{
    /// <summary>Gets the authorized opaque session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the compare-and-swap version token.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the UTC creation timestamp of the first stored snapshot for this session.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the UTC timestamp of the most recent stored snapshot for this session.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets the optional UTC expiration applied through the TTL index.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>One bounded, deterministically ordered page of authorized session summaries.</summary>
public sealed record MongoDBAgentSessionPage
{
    /// <summary>Gets the returned session summaries in ascending <see cref="MongoDBAgentSessionSummary.SessionId"/> order.</summary>
    public required IReadOnlyList<MongoDBAgentSessionSummary> Items { get; init; }

    /// <summary>Gets the opaque continuation token for the next page, or <see langword="null"/> when this is the last page.</summary>
    public string? ContinuationToken { get; init; }
}
