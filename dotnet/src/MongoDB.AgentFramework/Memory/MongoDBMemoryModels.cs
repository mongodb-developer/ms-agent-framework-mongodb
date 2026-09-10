using Microsoft.Extensions.AI;

namespace MongoDB.AgentFramework;

/// <summary>A scored semantic Memory search result.</summary>
public sealed record MongoDBMemorySearchResult(
    string MemoryId,
    ChatMessage Message,
    double Score,
    string? SessionId);

/// <summary>Content-free administrative metadata for one memory.</summary>
public sealed record MongoDBMemoryMetadata(
    string MemoryId,
    string Role,
    DateTimeOffset CreatedAt,
    string? ApplicationId,
    string? AgentId,
    string? UserId,
    string? SessionId,
    DateTimeOffset? ExpiresAt);

/// <summary>A bounded keyset-paginated metadata page.</summary>
public sealed record MongoDBMemoryMetadataPage(
    IReadOnlyList<MongoDBMemoryMetadata> Items,
    string? NextCursor);
