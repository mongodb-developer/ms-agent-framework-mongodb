# Developer documentation

This documentation explains the implemented system at the code level. The
[specifications](../spec/README.md) remain normative, and the
[architectural decisions](../decisions/README.md) record rationale.

## Foundation

- [Python package, client ownership, and lifecycle](foundation/python-client-ownership.md)
- [Python shared validation mechanics](foundation/python-validation.md)
- [.NET package and contract verification](foundation/dotnet-contract-research.md)
- [.NET foundation and shared internals](foundation/dotnet-foundation.md)

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

## Index Management

- [.NET Index Management implementation](index-management/dotnet-index-management.md)

## Ingestion

- [.NET Ingestion samples implementation](ingestion/dotnet-ingestion-samples.md)

## Persistence

- [.NET Session Store contract verification](persistence/dotnet-contract-research.md)
- [.NET Session Store implementation](persistence/dotnet-session-store.md)
- [.NET Workflow Checkpoint Store contract verification](persistence/dotnet-checkpoint-contract-research.md)
- [.NET Workflow Checkpoint Store implementation](persistence/dotnet-checkpoint-store.md)
