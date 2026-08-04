# .NET samples inventory

This document maps [`docs/spec/samples.md`](../../spec/samples.md)'s required
and recommended sample scenarios to what exists today under `dotnet/samples/`
and `dotnet/tests/IngestionSamples.Tests/`, as of
[implementation-map slice 20](../../spec/implementation-map.md) (packaging
and release engineering). It exists so the packaging/CI work in
[.NET packaging and release engineering](dotnet-packaging-release.md) has an
explicit, reviewable record of sample coverage rather than an implicit claim.
This packaging-only branch does **not** implement any missing sample; gaps
are recorded here as out of scope for this branch.

## Required scenarios (samples.md, "complete runnable quickstarts")

| Scenario | Status | Sample project |
| --- | --- | --- |
| .NET Memory | Present | `samples/MemoryQuickstart/` |
| .NET Chat History | Present | `samples/HistoryQuickstart/` |
| .NET RAG | Present | `samples/RAGQuickstart/` |
| .NET Session Store | Present | `samples/SessionPersistenceQuickstart/` |
| .NET Workflow Checkpoint Store | Present | `samples/WorkflowCheckpointResumeQuickstart/` |

All five required .NET quickstarts exist, build in Release
(`dotnet build --configuration Release`, verified as part of
`dotnet-quality.yml` and this branch's validation run), and are individually
documented (prerequisites, environment variables, index definitions, how to
run, expected output, cleanup) in the corresponding `## <Feature>` section of
[`dotnet/README.md`](../../../dotnet/README.md) and the linked developer
guide under `docs/development/<feature>/dotnet-*.md`.

## Equivalent-scenario samples ("where the framework capabilities exist")

| Scenario | Status | Sample project / notes |
| --- | --- | --- |
| `ParentDocumentRAG` | Present | `samples/ParentDocumentRAGQuickstart/` + `samples/IngestionSamples/ParentDocumentIngestionPipeline.cs`, `ParentDocumentRetriever.cs` |
| `IncrementalIngestion` | Present | `samples/IncrementalIngestionQuickstart/` + `samples/IngestionSamples/IncrementalIngestionPipeline.cs`, `IngestionDiffing.cs`, `SourceManifestReconciler.cs` |
| `OnDemandRetrievalTool` | **Missing** | No sample demonstrates a query-text-only model tool with application-owned retrieval policy. |
| `WorkflowRetrieval` | **Missing** | No sample demonstrates deterministic direct retrieval inside an Agent Framework workflow step (distinct from `WorkflowCheckpointResumeQuickstart`, which exercises checkpoint save/resume, not retrieval). |
| `MemoryAndRAG` | **Missing** | No sample demonstrates one agent using separate conversational Memory and authoritative RAG providers together. |
| `StructuredMetadataRetrieval` | **Missing** | No sample demonstrates a structured-output query plan translated to the `MongoDBRAGFilter` typed filter AST. |
| `MongoDBDocumentLoader` | **Missing** | `samples/IngestionSamples/` implements ingestion (write path: chunking, hashing, upserts) but no standalone sample demonstrates bounded cursor/pagination, projection, and cancellable *read-back* mapped to an ingestion-neutral document shape. |

`samples/IndexManagementQuickstart/` is an additional sample beyond the
`samples.md` list, demonstrating explicit Vector Search/Search index
provisioning and validation directly; it is documented in
[.NET Index Management developer guide](../index-management/dotnet-index-management.md).

## Disposition of the gaps

The five missing scenarios (`OnDemandRetrievalTool`, `WorkflowRetrieval`,
`MemoryAndRAG`, `StructuredMetadataRetrieval`, standalone
`MongoDBDocumentLoader`) are **explicitly out of scope for this
packaging/release engineering branch**. This branch's task is finalizing
package metadata, build determinism, symbols, API baseline, CI, SBOM, and
release documentation for the code that already exists -- not authoring new
sample scenarios. Implementing them is separate follow-up work against the
feature slices in `docs/spec/implementation-map.md`, not a packaging
concern, and is not silently claimed as complete here.

Every sample project that does exist was confirmed, as part of this
branch's validation, to build successfully in Release with zero errors, and
`dotnet-quality.yml` builds all nine of them explicitly on every pull
request so a future regression in any existing sample fails CI immediately.
