# Developer documentation

This documentation explains the implemented system at the code level. The
[specifications](../spec/README.md) remain normative, and the
[architectural decisions](../decisions/README.md) record rationale.

## Foundation

- [Python package, client ownership, and lifecycle](foundation/python-client-ownership.md)
- [Python shared validation mechanics](foundation/python-validation.md)

## Indexing

- [Python explicit index management](indexing/python-index-management.md)

## Memory

- [Python Memory implementation](memory/python-memory.md)

## Chat History

- [Python Chat History implementation](history/python-history.md)

## RAG

- [Python RAG contracts and typed filters](rag/python-contracts.md)
- [Python Vector Search implementation](rag/python-vector.md)
- [Python full-text Search implementation](rag/python-full-text.md)
- [Python native hybrid RRF implementation](rag/python-hybrid.md)

## Persistence

- [Persistence implementation index](persistence/README.md)
- [Python Session Store implementation](persistence/python-session-store.md)
- [Python Workflow Checkpoint Store implementation](persistence/python-checkpoints.md)

## Ingestion samples

- [Python sample ingestion](ingestion/python-sample-ingestion.md)

## Operations and security

- [Python observability and security](operations/python-observability-security.md)

## Packaging and release

- [Python packaging, compatibility, and release evidence](release/python-packaging.md)
