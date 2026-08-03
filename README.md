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
[`python`](python/README.md); do not depend on an unverified registry project
with the same name.

| Capability | Choose it for | Python sample |
| --- | --- | --- |
| Memory | scoped semantic recall from prior conversation | [Memory quickstart](python/samples/memory_quickstart.py) |
| Chat History | exact ordered replay of supported messages | [History quickstart](python/samples/history_quickstart.py) |
| RAG | read-only retrieval from pre-ingested knowledge | [Vector](python/samples/rag_vector_quickstart.py), [full text](python/samples/rag_full_text_quickstart.py), [hybrid](python/samples/rag_hybrid_quickstart.py) |
| Session Store | complete Agent Framework session snapshots | [Session persistence](python/samples/session_persistence.py) |
| Workflow Checkpoint Store | resumable workflow state and lineage | [Checkpoint resume](python/samples/workflow_checkpoint_resume.py) |

## Configuration and safety

Samples use `MONGODB_URI`, `MONGODB_DATABASE`, and feature-specific collection,
scope, and index variables documented in
[`python/samples/README.md`](python/samples/README.md). They validate setup before
network access and contain no credentials. Use separate least-privilege runtime,
index-provisioning, and sample-ingestion identities. Runtime RAG is read-only;
it does not ingest documents or accept model-generated BSON, filters, field
names, index names, or pipelines.

MongoDB Search, Vector Search, and native hybrid RRF require a compatible
deployment and pre-created indexes. Credentialed compatibility evidence is not
available in this repository yet, so publication remains blocked. See the
[Python compatibility evidence](docs/development/release/python-packaging.md)
and [release checklist](docs/release/python-release-checklist.md).

## Development

This repository is maintained under [`mongo/ms-agent-framework-mongodb`](https://github.com/mongo/ms-agent-framework-mongodb). See [docs/spec/README.md](docs/spec/README.md) for the canonical implementation specifications, [docs/spec/implementation-map.md](docs/spec/implementation-map.md) for implementation order, [docs/decisions/README.md](docs/decisions/README.md) for architectural decisions, and [CONTRIBUTING.md](CONTRIBUTING.md) for commit and validation requirements.

Python quickstarts and the explicitly sample-only, write-capable ingestion
demonstration are documented in [`python/README.md`](python/README.md). Runtime
RAG retrieval remains read-only and must use credentials separate from ingestion
and index provisioning.
