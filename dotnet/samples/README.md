# .NET scenario samples

These projects complement the existing quickstarts under `dotnet/samples`. Each new scenario sample supports a local preflight with `--validate-only` that checks arguments and environment variables before any MongoDB I/O.

| Sample | Feature | Writes | Cleanup |
| --- | --- | --- | --- |
| `MemoryQuickstart` | semantic Memory | one scoped Memory message and explicit index ensure | retains its message, collection, and index |
| `OnDemandRetrievalTool` | query-text-only AI tool over application-owned retrieval policy | none | none |
| `WorkflowRetrieval` | deterministic direct retrieval inside an Agent Framework workflow step | none | none |
| `MemoryAndRAG` | one agent combining conversational Memory and authoritative RAG | scoped Memory writes only | clears only the configured Memory session unless `--keep` |
| `StructuredMetadataRetrieval` | typed structured-output plan translated to `MongoDBRAGFilter` | none | none |
| `MongoDBDocumentLoader` | bounded MongoDB source paging into an ingestion-neutral document | none | none |
| `IncrementalIngestionQuickstart` | incremental chunk ingestion | scoped chunk writes | deletes sample chunks unless `--keep-data`; always drops its temporary index |
| `ParentDocumentRAGQuickstart` | parent-document retrieval | scoped parent and child writes | deletes sample chunks unless `--keep-data`; always drops its temporary index |

## Memory quickstart

Set `MONGODB_URI`, `MONGODB_DATABASE`, and optionally
`MONGODB_MEMORY_COLLECTION`, then run:

```powershell
dotnet run --project samples\MemoryQuickstart\MemoryQuickstart.csproj
```

The first write creates a fresh collection before index provisioning. The
sample then bounded-polls for Atlas indexing lag and prints `I prefer blue.`;
failure to recall the stored message is a timeout error, not a successful empty
result. The stored message, collection, and index remain for inspection.

## Retained ingestion runs

Set `MONGODB_URI`, `MONGODB_DATABASE`, and optionally
`MONGODB_INGESTION_COLLECTION`. Use `--keep-data` to retain sample records:

```powershell
dotnet run --project samples\IncrementalIngestionQuickstart\IncrementalIngestionQuickstart.csproj -- --keep-data
dotnet run --project samples\ParentDocumentRAGQuickstart\ParentDocumentRAGQuickstart.csproj -- --keep-data
```

For incremental ingestion, `--keep-data` also skips the changed/stale and
manifest-reconciliation demonstrations because those flows intentionally
delete records. Both samples still drop only the temporary generated Vector
Search index that their own run created.

## On-demand retrieval tool

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`, `MONGODB_RAG_SEARCH_INDEX`, and `MONGODB_RAG_TENANT`. The Search index must map `text` and `tenant_id`, and the collection must already contain tenant-scoped documents with `text` plus optional `source.name` / `source.url`.

```powershell
dotnet run --project samples\OnDemandRetrievalTool\OnDemandRetrievalTool.csproj -- --validate-only
dotnet run --project samples\OnDemandRetrievalTool\OnDemandRetrievalTool.csproj
```

Expected output is attributed text such as `[Access guide] Tenant access is enforced ...`. The tool schema exposes only `query`; tenant, index, filters, and limits remain application-owned. No documents or indexes are created, updated, or deleted.

## Workflow retrieval

Use the same environment and Search index contract as `OnDemandRetrievalTool`.

```powershell
dotnet run --project samples\WorkflowRetrieval\WorkflowRetrieval.csproj -- --validate-only
dotnet run --project samples\WorkflowRetrieval\WorkflowRetrieval.csproj
```

Expected output is the authorized retrieval result yielded from a workflow step. The workflow always queries MongoDB directly from its bound executor; no model chooses whether or how retrieval runs. Cleanup is not required because the sample is read-only.

## Memory plus RAG

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_MEMORY_COLLECTION`, `MONGODB_MEMORY_USER_ID`, `MONGODB_MEMORY_SESSION_ID`, `MONGODB_RAG_COLLECTION`, `MONGODB_RAG_VECTOR_INDEX`, and `MONGODB_RAG_TENANT`. Use a unique Memory user/session. Memory and RAG both use deterministic three-dimensional sample vectors, so the Memory collection and the preloaded RAG documents must share a compatible three-dimension cosine Vector Search index. The RAG collection must already contain tenant-scoped authoritative documents with `text`, `embedding`, `tenant_id`, and optional `source.name` / `source.url`.

```powershell
dotnet run --project samples\MemoryAndRAG\MemoryAndRAG.csproj -- --validate-only
dotnet run --project samples\MemoryAndRAG\MemoryAndRAG.csproj
dotnet run --project samples\MemoryAndRAG\MemoryAndRAG.csproj -- --keep
```

Expected output reports separate Memory and RAG context in one agent response. The sample seeds one scoped Memory turn, persists the run through `MongoDBMemoryProvider`, and clears only `MONGODB_MEMORY_SESSION_ID` unless `--keep` is passed. It never drops a collection or index.

## Structured metadata retrieval

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`, `MONGODB_RAG_SEARCH_INDEX`, and `MONGODB_RAG_TENANT`. The sample deserializes a closed structured plan (`query`, `category`, `visibility`) and translates it to `MongoDBRAGFilter.Equal` plus `MongoDBRAGFilter.In`; it never accepts raw BSON, operators, field paths, or pipelines from structured output. The Search index must map `text`, `tenant_id`, `metadata.category`, and `visibility`.

```powershell
dotnet run --project samples\StructuredMetadataRetrieval\StructuredMetadataRetrieval.csproj -- --validate-only
dotnet run --project samples\StructuredMetadataRetrieval\StructuredMetadataRetrieval.csproj
```

Expected output is up to three authorized `security` results showing category and visibility. The sample is read-only and performs no cleanup.

## Bounded MongoDB document loader

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_INGESTION_SOURCE_COLLECTION`, and a unique `MONGODB_RAG_SAMPLE_PREFIX` beginning with `sample-` or `test-`. Optional field-path overrides are `MONGODB_INGESTION_SOURCE_ID_FIELD`, `MONGODB_INGESTION_CONTENT_FIELD`, `MONGODB_INGESTION_TITLE_FIELD`, `MONGODB_INGESTION_URL_FIELD`, `MONGODB_INGESTION_METADATA_FIELD`, `MONGODB_INGESTION_TENANT_FIELD`, and `MONGODB_INGESTION_DELETED_FIELD`. Source records must expose those fields and store metadata as a document.

```powershell
dotnet run --project samples\MongoDBDocumentLoader\MongoDBDocumentLoader.csproj -- --validate-only
dotnet run --project samples\MongoDBDocumentLoader\MongoDBDocumentLoader.csproj -- --page-size 100 --max-documents 10
dotnet run --project samples\MongoDBDocumentLoader\MongoDBDocumentLoader.csproj -- --page-size 100 --cancel-after-documents 2
```

Expected output prints at most the requested number of mapped ingestion-neutral records in ascending source-ID order. Before yielding anything, the loader runs a bounded duplicate-ID aggregate, uses `simple` binary collation, projects only the required fields, and pages with keyset bounds under the configured prefix. It performs no writes or cleanup.
