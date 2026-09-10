# .NET samples inventory

This document maps [`docs/spec/samples.md`](../../spec/samples.md)'s required
and recommended sample scenarios to what exists today under `dotnet/samples/`
and `dotnet/tests/IngestionSamples.Tests/`. It exists so packaging and CI work in
[.NET packaging and release engineering](dotnet-packaging-release.md) has an
explicit, reviewable record of sample coverage rather than an implicit claim.

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
| `OnDemandRetrievalTool` | Present | `samples/OnDemandRetrievalTool/` |
| `WorkflowRetrieval` | Present | `samples/WorkflowRetrieval/` |
| `MemoryAndRAG` | Present | `samples/MemoryAndRAG/` |
| `StructuredMetadataRetrieval` | Present | `samples/StructuredMetadataRetrieval/` |
| `MongoDBDocumentLoader` | Present | `samples/MongoDBDocumentLoader/` |

`samples/IndexManagementQuickstart/` is an additional sample beyond the
`samples.md` list, demonstrating explicit Vector Search/Search index
provisioning and validation directly; it is documented in
[.NET Index Management developer guide](../index-management/dotnet-index-management.md).

Every listed sample project was confirmed to build successfully with zero
errors. The sample validation and quality workflows build the solution so a
future compilation regression in any sample fails CI.
