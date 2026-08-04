# ms-agent-framework-mongodb

MongoDB providers for Microsoft Agent Framework in Python and .NET.

Choose **Memory** for scoped semantic conversation recall, **Chat History** for an
exact ordered transcript, **RAG** for read-only authoritative knowledge retrieval,
**Session Store** for complete agent sessions, and **Workflow Checkpoint Store** for
resumable workflow state and lineage. Applications may combine these deliberately;
none substitutes for another.

## Python package

The canonical distribution is `agent-framework-mongodb` and the import root is
`agent_framework_mongodb`:

```powershell
python -m pip install agent-framework-mongodb
```

No package has been published from this repository yet. Until publishing
ownership is confirmed, build and install the reviewed artifact from
[`python`](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/README.md); do not depend on an unverified registry project
with the same name.

| Capability | Choose it for | Python sample |
| --- | --- | --- |
| Memory | scoped semantic recall from prior conversation | [Memory quickstart](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/memory_quickstart.py) |
| Chat History | exact ordered replay of supported messages | [History quickstart](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/history_quickstart.py) |
| RAG | read-only retrieval from pre-ingested knowledge | [Vector](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/rag_vector_quickstart.py), [full text](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/rag_full_text_quickstart.py), [hybrid](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/rag_hybrid_quickstart.py) |
| Session Store | complete Agent Framework session snapshots | [Session persistence](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/session_persistence.py) |
| Workflow Checkpoint Store | resumable workflow state and lineage | [Checkpoint resume](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/workflow_checkpoint_resume.py) |

Implementation-owned Python scenarios are also available for
[parent-document RAG](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/rag_parent_document.py),
[on-demand retrieval](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/on_demand_retrieval_tool.py),
[workflow retrieval](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/workflow_retrieval.py),
[Memory with RAG](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/memory_and_rag.py),
[structured metadata](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/structured_metadata_retrieval.py), and the
[bounded document loader](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/document_loader.py). They use local
model-free fixtures where a model client would otherwise require an
owner-selected provider.

## Configuration and safety

Samples use `MONGODB_URI`, `MONGODB_DATABASE`, and feature-specific collection,
scope, and index variables documented in
[`python/samples/README.md`](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/samples/README.md). They validate setup before
network access and contain no credentials. Use separate least-privilege runtime,
index-provisioning, and sample-ingestion identities. Runtime RAG is read-only;
it does not ingest documents or accept model-generated BSON, filters, field
names, index names, or pipelines.

MongoDB Search, Vector Search, and native hybrid RRF require a compatible
deployment and pre-created indexes. Credentialed compatibility evidence is not
available in this repository yet, so publication remains blocked. See the
[Python compatibility evidence](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/docs/development/release/python-packaging.md)
and [release checklist](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/docs/release/python-release-checklist.md).
Maintainers use the [Python release runbook](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/docs/release/python-release.md);
the documented local rehearsal never publishes.

## Development

This repository is maintained under [`mongo/ms-agent-framework-mongodb`](https://github.com/mongo/ms-agent-framework-mongodb). See [implementation specifications](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/docs/spec/README.md), the [implementation map](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/docs/spec/implementation-map.md), [architectural decisions](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/docs/decisions/README.md), and [contribution requirements](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/CONTRIBUTING.md).

Implemented provider guides:

- [Python Chat History](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/docs/development/history/python-history.md)
- [.NET Chat History](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/docs/development/history/dotnet-history.md)
- [.NET Session Store](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/docs/development/persistence/dotnet-session-store.md)

Python quickstarts and the explicitly sample-only, write-capable ingestion
demonstration are documented in [`python/README.md`](https://github.com/mongo/ms-agent-framework-mongodb/blob/main/python/README.md). Runtime
RAG retrieval remains read-only and must use credentials separate from ingestion
and index provisioning.
