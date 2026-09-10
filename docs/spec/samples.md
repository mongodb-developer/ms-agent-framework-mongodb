# Samples and Documentation

## Samples and documentation

The external repository must contain complete runnable quickstarts for both languages and all five public features:

- Python Memory
- Python Chat History
- Python RAG
- .NET Memory
- .NET Chat History
- .NET RAG
- Python Session Store
- Python Workflow Checkpoint Store
- .NET Session Store
- .NET Workflow Checkpoint Store

It MUST also contain equivalent Python and .NET scenario samples where the framework capabilities exist:

- `ParentDocumentRAG`: child chunk search with authorized, bounded parent hydration
- `OnDemandRetrievalTool`: query-text-only model tool with application-owned retrieval policy
- `WorkflowRetrieval`: deterministic direct retrieval inside an Agent Framework workflow step
- `MemoryAndRAG`: one agent using separate conversational Memory and authoritative RAG providers
- `StructuredMetadataRetrieval`: structured-output query plan translated to the typed filter AST
- `IncrementalIngestion`: deterministic IDs, content hashes, changed-document upsert, deletion/tombstone handling, and
  index readiness; explicitly sample-grade rather than a production pipeline
- `MongoDBDocumentLoader`: bounded cursor/pagination, projection, async cancellation, mapping to an ingestion-neutral
  sample document, and no arbitrary model-controlled query
- `SessionPersistence`: save, reload, compare-and-swap, expiration, and authorized deletion
- `WorkflowCheckpointResume`: pending approval, resume, lineage, latest lookup, pagination, and cleanup

Each sample must document prerequisites, environment variables, index definitions, model/embedding dimensions, how to
run it, expected output, and cleanup behavior.

The root README must explain when to choose Memory, exact Chat History, RAG, Session Store, Workflow Checkpoint Store,
or a deliberate combination. It must not imply that RAG learns from conversations, that Memory reconstructs an exact
transcript, or that History contains all provider-owned session/workflow state.

The Microsoft Agent Framework repository should retain or add lightweight discovery samples and documentation similar
to the Neo4j integration. Those files should consume the published external packages rather than project/workspace
references. Full implementation, provider tests, and release automation belong in `mongo/ms-agent-framework-mongodb`.

Microsoft Learn documentation should include separate pages or clearly separated sections for:

- MongoDB Memory provider
- MongoDB Chat History provider
- MongoDB RAG provider
- Session Store and Workflow Checkpoint Store
- index setup and deployment requirements
- Python and .NET usage
- Memory versus RAG selection guidance

Documentation publication is coordinated separately from package implementation and may require contribution to the
Microsoft Learn documentation repository.
