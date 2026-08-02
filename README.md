# ms-agent-framework-mongodb

MongoDB providers for Microsoft Agent Framework in Python and .NET.

Choose **Memory** for scoped semantic conversation recall, **Chat History** for an
exact ordered transcript, **RAG** for read-only authoritative knowledge retrieval,
**Session Store** for complete agent sessions, and **Workflow Checkpoint Store** for
resumable workflow state and lineage. Applications may combine these deliberately;
none substitutes for another.

This repository is maintained under [`mongo/ms-agent-framework-mongodb`](https://github.com/mongo/ms-agent-framework-mongodb). See [docs/spec/README.md](docs/spec/README.md) for the canonical implementation specifications, [docs/spec/implementation-map.md](docs/spec/implementation-map.md) for implementation order, [docs/decisions/README.md](docs/decisions/README.md) for architectural decisions, and [CONTRIBUTING.md](CONTRIBUTING.md) for commit and validation requirements.

Implemented provider guides:

- [Python Chat History](docs/development/history/python-history.md)
- [.NET Chat History](docs/development/history/dotnet-history.md)
