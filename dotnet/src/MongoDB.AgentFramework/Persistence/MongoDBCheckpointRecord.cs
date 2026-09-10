namespace MongoDB.AgentFramework;

/// <summary>A complete, authorized workflow checkpoint record and its lineage/sequence metadata.</summary>
public sealed record MongoDBCheckpointRecord
{
    /// <summary>Gets the workflow run/session partition this checkpoint belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the unique checkpoint identifier within <see cref="SessionId"/>.</summary>
    public required string CheckpointId { get; init; }

    /// <summary>Gets the parent checkpoint identifier, or <see langword="null"/> for a root checkpoint.</summary>
    public string? ParentCheckpointId { get; init; }

    /// <summary>
    /// Gets the monotonically allocated, atomically incremented sequence number that establishes commit order
    /// within <see cref="SessionId"/> independent of wall-clock timestamps.
    /// </summary>
    public required long Sequence { get; init; }

    /// <summary>Gets the exact framework-produced checkpoint JSON payload bytes, stored and returned verbatim.</summary>
    public required System.Text.Json.JsonElement Payload { get; init; }

    /// <summary>Gets the UTC creation timestamp of this checkpoint.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the optional UTC expiration applied through the TTL index.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Metadata-only summary of a stored checkpoint, omitting the payload. Returned by list operations.</summary>
public sealed record MongoDBCheckpointSummary
{
    /// <summary>Gets the workflow run/session partition this checkpoint belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the unique checkpoint identifier within <see cref="SessionId"/>.</summary>
    public required string CheckpointId { get; init; }

    /// <summary>Gets the parent checkpoint identifier, or <see langword="null"/> for a root checkpoint.</summary>
    public string? ParentCheckpointId { get; init; }

    /// <summary>Gets the monotonically allocated sequence number.</summary>
    public required long Sequence { get; init; }

    /// <summary>Gets the UTC creation timestamp of this checkpoint.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the optional UTC expiration applied through the TTL index.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>One bounded, deterministically ordered page of checkpoint summaries.</summary>
public sealed record MongoDBCheckpointPage
{
    /// <summary>Gets the returned checkpoint summaries in ascending <see cref="MongoDBCheckpointSummary.Sequence"/> order.</summary>
    public required IReadOnlyList<MongoDBCheckpointSummary> Items { get; init; }

    /// <summary>
    /// Gets the opaque, scoped, versioned, tamper-rejecting continuation token for the next page, or
    /// <see langword="null"/> when this is the last page. Passing a token issued by a differently scoped
    /// <see cref="MongoDBCheckpointStore"/> (different tenant/workflow), or one that has been altered, throws
    /// <see cref="MongoDBConfigurationException"/> rather than silently returning wrong-scope or skipped data.
    /// </summary>
    public string? ContinuationToken { get; init; }
}
