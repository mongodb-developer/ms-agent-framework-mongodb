using Microsoft.Agents.AI;
using System.Text.Json;

namespace MongoDB.AgentFramework.Internal.Persistence;

/// <summary>
/// Narrow seam between <see cref="MongoDBAgentSessionStore"/>'s storage envelope and the public Agent Framework
/// API used to turn an <see cref="AgentSession"/> into/from its JSON representation. As of
/// <c>Microsoft.Agents.AI.Abstractions</c> 1.13.0 (verified current through 1.16.0; see
/// docs/development/persistence/dotnet-contract-research.md) there is no public session-hosting persistence
/// contract, so <see cref="AIAgentSessionCodec"/> is the only implementation, wrapping
/// <see cref="AIAgent.SerializeSessionAsync"/>/<see cref="AIAgent.DeserializeSessionAsync"/>. If a future package
/// version publishes a dedicated session-hosting contract, add a second implementation of this interface (and a
/// corresponding store constructor) without changing the stored BSON envelope, schema version, or any existing
/// public store method.
/// </summary>
internal interface IAgentSessionCodec
{
    /// <summary>Serializes an agent session to its public JSON representation.</summary>
    ValueTask<JsonElement> SerializeAsync(AgentSession session, CancellationToken cancellationToken);

    /// <summary>Deserializes an agent session from its public JSON representation.</summary>
    ValueTask<AgentSession> DeserializeAsync(JsonElement element, CancellationToken cancellationToken);
}

/// <summary>
/// Wraps the public <see cref="AIAgent"/> session serialization API. An <see cref="AIAgent"/> instance is
/// required because <see cref="AgentSession"/> JSON shape is agent-defined; the framework does not expose a
/// serializer that is independent of the originating agent.
/// </summary>
internal sealed class AIAgentSessionCodec(AIAgent agent, JsonSerializerOptions? serializerOptions)
    : IAgentSessionCodec
{
    private readonly AIAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public ValueTask<JsonElement> SerializeAsync(AgentSession session, CancellationToken cancellationToken) =>
        _agent.SerializeSessionAsync(session, serializerOptions, cancellationToken);

    public ValueTask<AgentSession> DeserializeAsync(JsonElement element, CancellationToken cancellationToken) =>
        _agent.DeserializeSessionAsync(element, serializerOptions, cancellationToken);
}
