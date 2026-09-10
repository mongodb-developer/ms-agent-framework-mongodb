# Project Scope and Decisions

## Decision summary

- Use one external repository named `mongo/ms-agent-framework-mongodb`.
- Keep Memory, Chat History, RAG, Session Store, and Workflow Checkpoint Store as separate public modules and provider
   types.
- Ship the stable feature modules in one Python distribution and one .NET package.
- Support Python and .NET as equal product surfaces.
- Depend only on public Microsoft Agent Framework interfaces.
- Keep the Microsoft Agent Framework repository focused on lightweight discovery samples and documentation links.
- Use the implementation on the current `feature/mongodb-memory` branch as the source prototype for Memory.
- Do not publish an externally owned .NET package under a `Microsoft.*` namespace.

The split is by behavior, not by repository:

| Feature | Reads | Writes | Primary data | Purpose |
| --- | --- | --- | --- | --- |
| Memory | Yes | Yes | Agent conversation messages | Recall relevant prior interactions across sessions |
| Chat History | Yes | Yes | Ordered messages for one session | Reconstruct the exact conversation sent to the model |
| RAG | Yes | No | Pre-ingested knowledge documents/chunks | Ground responses in an existing knowledge base |
| Session Store | Yes | Yes | Serialized `AgentSession` snapshot | Resume a stateless hosted agent with all session state |
| Workflow Checkpoint | Yes | Yes | Workflow state and checkpoint lineage | Resume interrupted or human-in-the-loop workflows |

These concepts MUST remain distinct:

- **Memory** selects semantically related information and may cross session boundaries within an authorized scope.
- **Chat History** returns exact messages for one session in deterministic order and performs no similarity search.
- **Session Store** serializes the complete framework session, not only messages.
- **Workflow Checkpoint Store** persists workflow execution state and lineage, not an agent conversation transcript.
- **RAG** retrieves authoritative pre-ingested knowledge and performs no runtime writes.

The modules may share internal MongoDB client ownership, serialization, retention, index inspection, filtering, and
test infrastructure. They MUST NOT share a public provider class or mix lifecycle semantics.

## Why use an external repository

The MongoDB integration has a product lifecycle that differs from Microsoft Agent Framework core:

- MongoDB server, driver, Search, Vector Search, and deployment capabilities evolve independently.
- MongoDB-specific defects and feature requests need a clear ownership location.
- Python and .NET packages should be released without waiting for an Agent Framework monorepo release.
- Memory and RAG benefit from shared MongoDB-specific implementation and integration-test infrastructure.
- The Agent Framework monorepo should not carry provider-specific query pipelines or connector constraints in core.

An external repository is appropriate only with an identified maintainer and package-publishing owner. The GitHub
organization is `mongo`; before the first public release, record the support team, NuGet owner, PyPI owner, security
contact, and support policy in the repository.

## Repository layout

Use one cross-language repository:

```text
ms-agent-framework-mongodb/
├── .github/
│   ├── workflows/
│   ├── CODEOWNERS
│   └── dependabot.yml
├── docs/
│   ├── compatibility.md
│   ├── memory.md
│   ├── chat-history.md
│   ├── rag.md
│   ├── indexing.md
│   ├── persistence.md
│   └── decisions/
├── python/
│   ├── pyproject.toml
│   ├── src/
│   │   └── agent_framework_mongodb/
│   │       ├── __init__.py
│   │       ├── memory/
│   │       ├── history/
│   │       ├── rag/
│   │       ├── session_store/
│   │       ├── checkpointing/
│   │       └── _shared/
│   ├── tests/
│   │   ├── unit/
│   │   └── integration/
│   └── samples/
│       ├── memory/
│       ├── history/
│       ├── persistence/
│       └── rag/
├── dotnet/
│   ├── src/
│   │   └── MongoDB.AgentFramework/
│   ├── tests/
│   │   ├── MongoDB.AgentFramework.UnitTests/
│   │   └── MongoDB.AgentFramework.IntegrationTests/
│   └── samples/
│       ├── Memory/
│       ├── ChatHistory/
│       ├── Persistence/
│       └── RAG/
├── LICENSE
├── README.md
├── SECURITY.md
└── PROJECT.MD
```

`_shared` and internal .NET types are implementation details. Public callers should learn the Memory, Chat History,
RAG, or explicit persistence module, not a collection of low-level MongoDB helpers.

## Neo4j reference model and MongoDB adaptation

The implementation SHOULD follow the repository and provider shape demonstrated by
[`neo4j-labs/neo4j-maf-provider`](https://github.com/neo4j-labs/neo4j-maf-provider), specifically:

- one external repository containing Python and .NET implementations
- language-specific packages, tests, samples, and release workflows
- one Agent Framework context provider per language
- explicit full-text, vector, and hybrid retrievers behind the RAG provider
- provider-owned connection lifecycle when the provider constructs its client
- independent PyPI and NuGet versions and releases
- shared setup documentation and equivalent sample scenarios across languages

This project intentionally differs in two ways:

1. The same repository also contains a distinct Memory provider.
2. Retrieval MUST use MongoDB-native document, Search, Vector Search, aggregation, filtering, and rank-fusion
   capabilities rather than reproducing graph traversal or Cypher concepts.

[`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory) is a product-separation reference, not a feature
specification. The following Neo4j Agent Memory features are out of scope:

- entity and relationship extraction
- graph construction and graph traversal
- entity resolution and deduplication
- fact and preference inference
- reasoning traces and tool-use graphs
- graph algorithms and geospatial graph queries
- memory consolidation and background enrichment
- a hosted memory backend or MCP server

MongoDB-specific long-term fact memory is out of scope. Adding it requires a new module, data model, and ADR; it MUST
NOT be smuggled into semantic chat-history Memory or read-only RAG.

### Reference revision

Initial design comparison used Neo4j GraphRAG repository revision
[`b1aadb6`](https://github.com/neo4j-labs/neo4j-maf-provider/tree/b1aadb6d5316665b32b635f481fc749bf0eaf5d7).
Implementation SHOULD re-check the latest upstream revision before copying any interface pattern. Source examples are
evidence, not a dependency, and no Neo4j code should be copied without confirming license obligations.

## Shared design principles

1. **Use public framework seams.** Implement Agent Framework context-provider interfaces; do not modify core solely
   to accommodate MongoDB key types, schemas, or query behavior.
2. **Keep provider interfaces small.** Hide MongoDB aggregation pipelines, embedding calls, score mapping, resource
   ownership, and index inspection behind the provider.
3. **Accept dependencies.** Support caller-supplied clients or collections for custom configuration and testability.
4. **Make ownership explicit.** Dispose only clients created by the provider. Never dispose caller-owned clients,
   databases, or collections.
5. **Separate configuration errors from operational errors.** Invalid fields, dimensions, scopes, modes, and index
   definitions must fail clearly. Agent invocation may optionally degrade to no additional context on transient
   retrieval failures, but public search methods must surface the original failure.
6. **Do not create indexes implicitly during normal retrieval.** Index creation is an explicit deployment/startup
  operation because MongoDB Search index creation is asynchronous and requires elevated permissions.
7. **Preserve source information.** RAG results must carry enough information for citations and diagnostics.
8. **Secure filters are mandatory when configured.** Tenant/security filters must execute inside the retrieval stage
   where supported, before result limiting. Do not retrieve globally and filter in application memory.
9. **No silent capability downgrade.** Unsupported exact, full-text, or hybrid behavior must produce an actionable
   error rather than silently switching search modes.
10. **Do not log secrets or retrieved content by default.** Connection strings, credentials, embeddings, user text,
    and document contents are sensitive.

## Non-goals

The initial project will not:

- modify Microsoft Agent Framework core types for MongoDB
- create a separate repository for Memory and another for RAG
- treat semantic Memory as exact Chat History, or exact Chat History as a complete Session Store
- implement a new embedding model or chat client
- provide automatic production document ingestion or chunking
- execute arbitrary model-generated MongoDB queries
- expose a general-purpose MongoDB agent database toolkit
- expose generic key-value/document/byte stores without a separate contract and ADR
- claim graph traversal or call the MongoDB provider GraphRAG; parent lookup or `$graphLookup` alone is not GraphRAG
- perform fact extraction, memory consolidation, user profiling, or knowledge-graph construction
- guarantee cross-language access to one physical Memory collection until schema interoperability is explicitly tested
- guarantee cross-language exact-history/session/checkpoint interoperability until serialization fixtures prove it
- use server-side automated embeddings; vector and hybrid retrieval require a caller-provided embedding generator
- provide a runtime retrieval cache; caching remains application middleware
- silently emulate unavailable MongoDB Search features in application memory

## Resolved implementation decisions

| Area | Decision | Record |
| --- | --- | --- |
| Repository | Use `mongo/ms-agent-framework-mongodb` for both languages. | [ADR 0001](../../decisions/0001-use-one-external-cross-language-repository.md) |
| Product boundaries | Keep all five features separate and keep RAG runtime paths read-only. | [ADR 0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md) |
| Packages | Publish Python `agent-framework-mongodb`/`agent_framework_mongodb` and .NET `MongoDB.AgentFramework` independently. | [ADR 0004](../../decisions/0004-publish-independent-language-packages.md) |
| Search and release order | Require typed filters, native ANN, ENN, full-text, and hybrid RRF pipelines, delivered through feature gates. | [ADR 0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md), [ADR 0011](../../decisions/0011-release-features-through-staged-quality-gates.md) |
| Memory failures | Memory persistence fails open only at the agent adapter boundary by default; direct APIs fail to callers. | [ADR 0015](../../decisions/0015-default-memory-persistence-to-fail-open.md) |
| Language parity | Require equivalent observable behavior, not physical collection identity. | [ADR 0009](../../decisions/0009-enforce-behavioral-not-physical-parity.md) |
| Index APIs | Keep explicit feature-specific index facades in runtime packages; never provision implicitly. | [ADR 0016](../../decisions/0016-keep-index-facades-in-runtime-packages.md) |
| Telemetry | Use standard logging and tracing without unapproved integration markers. | [ADR 0017](../../decisions/0017-use-standard-telemetry-without-unapproved-markers.md) |
| Exact History | Use versioned exact payloads with atomic ordering and idempotency. | [ADR 0008](../../decisions/0008-store-versioned-exact-history-with-atomic-ordering.md) |
| Persistence scope | Include Session Store and Workflow Checkpoint Store as required public modules. | [ADR 0012](../../decisions/0012-include-session-and-checkpoint-stores.md) |
| Persistence compatibility | Use only supported public serializers/contracts and reject incompatible versions. | [ADR 0018](../../decisions/0018-version-gate-persistence-contracts.md) |

The MIT [license](../../../LICENSE) and [contribution policy](../../../CONTRIBUTING.md) are present. The support team,
PyPI and NuGet publishing identities, security contact, and exact supported Agent Framework, driver, and MongoDB
deployment versions MUST be supplied by owners or verified compatibility evidence. These are Foundation verification
inputs and package-publication prerequisites; contributors MUST NOT invent them, and their absence does not block
feature coding against a verified public contract.

[Back to the specification index](../README.md)
