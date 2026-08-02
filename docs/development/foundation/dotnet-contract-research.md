# .NET package and contract verification

This note records the primary-source verification performed on 2026-08-01 for
[Foundation and shared internals](../../spec/implementation-map.md) and
[Memory .NET](../../spec/implementation-map.md). The
[package specification](../../spec/packages.md) remains normative.

## Verified dependencies

- `Microsoft.Agents.AI.Abstractions` 1.16.0 is the current stable Agent Framework
  abstractions package and targets .NET 8, .NET 9, and .NET 10.
- `Microsoft.Extensions.AI.Abstractions` 10.8.3 is the current stable embedding
  abstractions package.
- `MongoDB.Driver` 3.10.0 is the current stable MongoDB .NET/C# driver.
- No stable, non-Semantic-Kernel MongoDB connector for
  `Microsoft.Extensions.VectorData` is published. Memory therefore uses the
  MongoDB driver directly rather than taking a preview Semantic Kernel dependency.

Sources:

- [Microsoft.Agents.AI.Abstractions NuGet registration](https://api.nuget.org/v3/registration5-semver1/microsoft.agents.ai.abstractions/index.json)
- [Microsoft.Extensions.AI.Abstractions NuGet registration](https://api.nuget.org/v3/registration5-semver1/microsoft.extensions.ai.abstractions/index.json)
- [MongoDB.Driver NuGet versions](https://api.nuget.org/v3-flatcontainer/mongodb.driver/index.json)
- [MongoDB .NET/C# Driver source](https://github.com/mongodb/mongo-csharp-driver)

## Verified Agent Framework contracts

The public `AIContextProvider` contract in
[microsoft/agent-framework](https://github.com/microsoft/agent-framework) exposes
sealed invocation entry points and protected `ProvideAIContextAsync` and
`StoreAIContextAsync` override points. The .NET Memory provider must override
those protected methods so the framework retains filtering, source attribution,
context merging, exception handling, and session-state behavior.

`MessageAIContextProvider` is an optional message-only specialization.
`TextSearchProvider` is sealed and cannot be subclassed. RAG must either compose
it after compatibility tests prove cancellation and result preservation or use a
dedicated `AIContextProvider` adapter.

The embedding dependency is
`IEmbeddingGenerator<string, Embedding<float>>`. It is caller-owned and must not
be disposed by a provider unless ownership is explicitly transferred.

Primary source:

- [Microsoft Agent Framework .NET source](https://github.com/microsoft/agent-framework/tree/main/dotnet/src)

## MongoDB driver surfaces

Driver 3.10 provides typed `VectorSearch`, `Search`, and `RankFusion` aggregation
APIs and explicit Search index management through
`IMongoCollection<T>.SearchIndexes`. Provider constructors and invocation hooks
must not create indexes; explicit index facades own provisioning and readiness.

Primary sources:

- [MongoDB Vector Search aggregation stage](https://www.mongodb.com/docs/vector-search/query/aggregation-stages/vector-search-stage/)
- [MongoDB .NET/C# Search index management](https://www.mongodb.com/docs/drivers/csharp/current/indexes/search-indexes/)

No public `Microsoft.Agents.AI.MongoDB` package or official MongoDB Agent
Framework implementation was found. The .NET integration is therefore a
greenfield implementation against these public contracts.
