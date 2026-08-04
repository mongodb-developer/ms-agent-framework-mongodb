# Developer documentation

This documentation explains the implemented system at the code level. The
[specifications](../spec/README.md) remain normative, and the
[architectural decisions](../decisions/README.md) record rationale.

## Foundation

- [Python package, client ownership, and lifecycle](foundation/python-client-ownership.md)
- [Python shared validation mechanics](foundation/python-validation.md)
- [.NET package and contract verification](foundation/dotnet-contract-research.md)
- [.NET foundation and shared internals](foundation/dotnet-foundation.md)

## Indexing

- [Python explicit index management](indexing/python-index-management.md)

## Memory

- [Python Memory implementation](memory/python-memory.md)
- [.NET Memory implementation](memory/dotnet-memory.md)

## Chat History

- [Python Chat History implementation](history/python-history.md)
- [.NET Chat History implementation](history/dotnet-history.md)

## RAG

- [.NET RAG contracts and typed filters](rag/dotnet-rag.md)
- [.NET Vector RAG (ANN/ENN) direct search and context adapter](rag/dotnet-rag-vector-search.md)
- [.NET FullText RAG direct search](rag/dotnet-rag-full-text-search.md)
- [.NET HybridRrf RAG direct search](rag/dotnet-rag-hybrid-rrf.md)
- [Python RAG contracts and typed filters](rag/python-contracts.md)
- [Python Vector Search implementation](rag/python-vector.md)
- [Python full-text Search implementation](rag/python-full-text.md)
- [Python native hybrid RRF implementation](rag/python-hybrid.md)

## Index Management

- [.NET Index Management implementation](index-management/dotnet-index-management.md)

## Ingestion

- [.NET Ingestion samples implementation](ingestion/dotnet-ingestion-samples.md)

## Persistence

- [.NET Session Store contract verification](persistence/dotnet-contract-research.md)
- [.NET Session Store implementation](persistence/dotnet-session-store.md)
- [.NET Workflow Checkpoint Store contract verification](persistence/dotnet-checkpoint-contract-research.md)
- [.NET Workflow Checkpoint Store implementation](persistence/dotnet-checkpoint-store.md)
- [Persistence implementation index](persistence/README.md)
- [Python Session Store implementation](persistence/python-session-store.md)
- [Python Workflow Checkpoint Store implementation](persistence/python-checkpoints.md)

## Observability and Security

- [.NET observability telemetry](observability-security/dotnet-telemetry.md)
- [.NET threat model](observability-security/dotnet-threat-model.md)
- [.NET least-privilege roles](observability-security/dotnet-least-privilege.md)
- [.NET TLS and network-access requirements](observability-security/dotnet-tls.md)
- [Python observability and security](operations/python-observability-security.md)

## Release Engineering

- [.NET packaging and release engineering](release/dotnet-packaging-release.md)
- [.NET Agent Framework compatibility matrix](release/dotnet-agent-framework-compatibility-matrix.md)
- [.NET release operations](release/dotnet-release-operations.md)
- [.NET samples inventory](release/dotnet-samples-inventory.md)
- [Python packaging, compatibility, and release evidence](release/python-packaging.md)
- [Python release runbook](../release/python-release.md)

## Python Ingestion Samples

- [Python sample ingestion](ingestion/python-sample-ingestion.md)
