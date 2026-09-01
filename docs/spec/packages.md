# Package and Namespace Requirements

## Package and namespace requirements

### Python

- Distribution name: `agent-framework-mongodb`.
- Import root: `agent_framework_mongodb`.
- Minimum Python version: match the currently supported Microsoft Agent Framework floor; the source prototype uses
  Python >=3.10.
- Primary dependencies:
  - `agent-framework-core`
  - `pymongo`
- Use PyMongo's asynchronous API. Do not add Motor for new code.

Public imports:

```python
from agent_framework_mongodb import (
  MongoDBHistoryProvider,
  MongoDBHistoryProviderOptions,
    MongoDBMemoryContextProvider,
    MongoDBRAGContextProvider,
    MongoDBRAGResult,
    MongoDBSearchMode,
    MongoDBSessionStore,
    MongoDBCheckpointStorage,
)
```

The current prototype exports `MongoDBContextProvider`. Rename it to `MongoDBMemoryContextProvider` before the first
public external release so the interface remains unambiguous after RAG is added. If an earlier package version has
already been published and used, retain `MongoDBContextProvider` as a deprecated alias for one documented transition
period.

### .NET

- NuGet package ID: `MongoDB.AgentFramework`.
- Namespace: `MongoDB.AgentFramework`.
- Target the same supported .NET TFMs as Microsoft Agent Framework, initially .NET 8, .NET 9, and .NET 10.
- Primary dependencies:
  - Microsoft Agent Framework abstractions
  - `Microsoft.Extensions.AI`
  - `Microsoft.Extensions.VectorData` where useful
  - `MongoDB.Driver`
  - MongoDB Vector Store connector only where it adds behavior without constraining RAG pipelines

The .NET Memory implementation SHOULD continue using `Microsoft.Extensions.VectorData` and the MongoDB Vector Store
connector where the connector's key, schema, filtering, and ownership behavior has been proven. RAG MUST use typed
MongoDB.Driver aggregation builders, or structured BSON for unsupported builder surfaces, because advanced Search,
`$rankFusion`, score metadata, and post-retrieval enrichment exceed a generic Vector Store contract. The public RAG
API MUST not expose VectorData limitations as MongoDB limitations.

Public types:

```csharp
MongoDBAgentSessionPage
MongoDBAgentSessionRecord
MongoDBAgentSessionStore
MongoDBAgentSessionStoreOptions
MongoDBAgentSessionSummary
MongoDBChatHistoryProvider
MongoDBChatHistoryProviderOptions
MongoDBCheckpointPage
MongoDBCheckpointRecord
MongoDBCheckpointStore
MongoDBCheckpointStoreOptions
MongoDBCheckpointSummary
MongoDBIndexComparison
MongoDBIndexInfo
MongoDBIndexStatus
MongoDBMemoryIndexManager
MongoDBMemoryMetadata
MongoDBMemoryMetadataPage
MongoDBMemoryProvider
MongoDBMemoryProviderOptions
MongoDBMemoryScope
MongoDBMemorySearchResult
MongoDBRAGContextProvider
MongoDBRAGContextProviderOptions
MongoDBRAGFilter
MongoDBRAGIndexManager
MongoDBRAGProvider
MongoDBRAGProviderOptions
MongoDBRAGResult
MongoDBSearchMode
MongoDBSearchIndexDefinition
MongoDBVectorSearchIndexDefinition
```

The package also exposes the `MongoDBIntegrationException` hierarchy for actionable configuration, capability,
concurrency, embedding, index, mapping, persistence, retrieval, and timeout failures. The complete member-level public
surface is enforced by `dotnet/src/MongoDB.AgentFramework/PublicAPI.Unshipped.txt`; this section documents its
consumer-facing type groups rather than duplicating every member signature.

The current prototype uses `Microsoft.Agents.AI.MongoDB` and `MongoDBProvider`. Those names are acceptable only inside
a Microsoft-owned and Microsoft-published package. During extraction, rename them to the canonical external package
and namespace.

Verify PyPI and NuGet package-name availability before publishing. A package rename after public release requires a
separate migration plan.
