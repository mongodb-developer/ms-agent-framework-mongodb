from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Sequence
from typing import Any

import pytest
from agent_framework import AgentSession, Embedding, GeneratedEmbeddings, Message, SessionContext
from pymongo.errors import OperationFailure

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBIndexMismatchError,
    MongoDBRAGContextProvider,
    MongoDBRAGParentOptions,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBRAGSearchOptions,
    MongoDBSearchMode,
)
from agent_framework_mongodb._shared.client import MongoClientHandle


class FakeEmbeddingGenerator:
    additional_properties: dict[str, Any] = {}

    def __init__(self) -> None:
        self.calls: list[list[str]] = []

    async def _generate(self, values: Sequence[str]) -> GeneratedEmbeddings[list[float], Any]:
        self.calls.append(list(values))
        return GeneratedEmbeddings([Embedding(vector=[1.0, 0.0, 0.5]) for _ in values])

    def get_embeddings(
        self,
        values: Sequence[str],
        *,
        options: Any | None = None,
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        del options
        return self._generate(values)


class FakeCursor:
    def __init__(self, documents: list[dict[str, Any]]) -> None:
        self.documents = documents

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        return self.documents if length is None else self.documents[:length]


class FakeCollection:
    def __init__(self) -> None:
        self.pipeline: list[dict[str, Any]] | None = None
        self.documents: list[dict[str, Any]] = []
        self.search_indexes: list[dict[str, Any]] = []
        self.read_error: Exception | None = None
        self.created_search_model: Any | None = None
        self.updated_search_definition: tuple[str, dict[str, Any]] | None = None

    async def aggregate(self, pipeline: list[dict[str, Any]]) -> FakeCursor:
        if self.read_error is not None:
            raise self.read_error
        self.pipeline = pipeline
        return FakeCursor(self.documents)

    async def list_search_indexes(self, *, name: str) -> FakeCursor:
        return FakeCursor([index for index in self.search_indexes if index.get("name") == name])

    async def create_search_index(self, model: Any) -> str:
        self.created_search_model = model
        return "knowledge_vector"

    async def update_search_index(self, name: str, definition: dict[str, Any]) -> None:
        self.updated_search_definition = (name, definition)


async def test_ann_search_embeds_and_executes_a_filtered_read_only_pipeline() -> None:
    collection = FakeCollection()
    collection.documents = [
        {
            "_id": "guide-1",
            "content": "Use a mandatory vector prefilter.",
            "source": {"name": "Security guide", "url": "https://example.test/security"},
            "kind": "guide",
            "_ragScore": 0.91,
        }
    ]
    embeddings = FakeEmbeddingGenerator()
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            num_candidates=25,
            filter=EqualFilter("tenant_id", "tenant-a"),
            metadata_fields=("kind",),
        ),
        embedding_generator=embeddings,
        collection=collection,  # type: ignore[arg-type]
        validate_index_before_search=False,
    )

    results = await provider.search("How is retrieval isolated?")

    assert embeddings.calls == [["How is retrieval isolated?"]]
    assert collection.pipeline == [
        {
            "$vectorSearch": {
                "index": "knowledge_vector",
                "path": "embedding",
                "queryVector": [1.0, 0.0, 0.5],
                "numCandidates": 25,
                "limit": 5,
                "filter": {"tenant_id": {"$eq": "tenant-a"}},
            }
        },
        {"$set": {"_ragScore": {"$meta": "vectorSearchScore"}}},
    ]
    assert len(results) == 1
    assert results[0].id == "guide-1"
    assert results[0].text == "Use a mandatory vector prefilter."
    assert results[0].source_name == "Security guide"
    assert results[0].source_url == "https://example.test/security"
    assert results[0].metadata == {"kind": "guide"}
    assert results[0].raw_document is collection.documents[0]


async def test_search_rejects_an_incompatible_index_before_embedding() -> None:
    collection = FakeCollection()
    collection.search_indexes = [
        {
            "name": "knowledge_vector",
            "type": "vectorSearch",
            "status": "READY",
            "queryable": True,
            "latestDefinition": {
                "fields": [
                    {
                        "type": "vector",
                        "path": "wrong_embedding",
                        "numDimensions": 3,
                        "similarity": "cosine",
                    },
                    {"type": "filter", "path": "tenant_id"},
                ]
            },
        }
    ]
    embeddings = FakeEmbeddingGenerator()
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            filter=EqualFilter("tenant_id", "tenant-a"),
        ),
        embedding_generator=embeddings,
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBIndexMismatchError, match="path"):
        await provider.search("query")

    assert embeddings.calls == []


async def test_enn_search_uses_exact_without_candidates_and_conjoins_call_filter() -> None:
    collection = FakeCollection()
    collection.documents = [{"_id": "doc-1", "content": "Exact result", "_ragScore": 0.8}]
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ENN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            filter=EqualFilter("tenant_id", "tenant-a"),
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
        validate_index_before_search=False,
    )

    await provider.search(
        "exact query",
        options=MongoDBRAGSearchOptions(
            top_k=2,
            filter=EqualFilter("metadata.kind", "reference"),
        ),
    )

    assert collection.pipeline is not None
    vector = collection.pipeline[0]["$vectorSearch"]
    assert vector == {
        "index": "knowledge_vector",
        "path": "embedding",
        "queryVector": [1.0, 0.0, 0.5],
        "exact": True,
        "limit": 2,
        "filter": {
            "$and": [
                {"tenant_id": {"$eq": "tenant-a"}},
                {"metadata.kind": {"$eq": "reference"}},
            ]
        },
    }
    assert "numCandidates" not in vector


async def test_before_run_injects_source_attributed_citation_context() -> None:
    collection = FakeCollection()
    collection.documents = [
        {
            "_id": "doc-1",
            "content": "Retrieved evidence",
            "source": {"name": "Guide", "url": "https://example.test/guide"},
            "_ragScore": 0.87,
        }
    ]
    direct = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
        validate_index_before_search=False,
    )
    rag = MongoDBRAGContextProvider(direct)
    context = SessionContext(
        input_messages=[
            Message("system", ["ignored"]),
            Message("user", ["first"]),
            Message("assistant", ["follow-up context"]),
            Message("user", ["current question"]),
        ]
    )

    await rag.before_run(
        agent=object(),
        session=AgentSession(),
        context=context,
        state={},
    )
    await rag.after_run(
        agent=object(),
        session=AgentSession(),
        context=context,
        state={},
    )

    injected = context.context_messages[rag.source_id][0]
    assert injected.text == "Retrieved evidence"
    assert injected.additional_properties["_attribution"] == {
        "source_id": "mongodb-rag",
        "source_type": "MongoDBRAGContextProvider",
    }
    annotations = injected.contents[0].annotations
    assert annotations is not None
    annotation = annotations[0]
    assert annotation.get("title") == "Guide"
    assert annotation.get("url") == "https://example.test/guide"
    assert context.instructions == [rag.context_prompt]
    assert collection.pipeline is not None
    assert collection.pipeline[0]["$vectorSearch"]["queryVector"] == [1.0, 0.0, 0.5]


async def test_adapter_fails_open_only_for_transient_retrieval_and_redacts_logs(
    caplog: pytest.LogCaptureFixture,
) -> None:
    collection = FakeCollection()
    collection.read_error = OperationFailure(
        "sensitive-host.invalid secret query",
        code=91,
    )
    direct = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
        validate_index_before_search=False,
    )
    rag = MongoDBRAGContextProvider(direct)
    context = SessionContext(input_messages=[Message("user", ["secret query"])])

    await rag.before_run(
        agent=object(),
        session=AgentSession(),
        context=context,
        state={},
    )

    assert context.context_messages == {}
    assert "secret query" not in caplog.text
    assert "sensitive-host" not in caplog.text


async def test_embedding_cancellation_propagates_through_direct_and_adapter() -> None:
    class CancellingEmbeddingGenerator(FakeEmbeddingGenerator):
        async def _generate(
            self,
            values: Sequence[str],
        ) -> GeneratedEmbeddings[list[float], Any]:
            del values
            raise asyncio.CancelledError

    direct = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=CancellingEmbeddingGenerator(),
        collection=FakeCollection(),  # type: ignore[arg-type]
        validate_index_before_search=False,
    )
    rag = MongoDBRAGContextProvider(direct)

    with pytest.raises(asyncio.CancelledError):
        await rag.before_run(
            agent=object(),
            session=AgentSession(),
            context=SessionContext(input_messages=[Message("user", ["query"])]),
            state={},
        )


async def test_index_provisioning_is_explicit_and_uses_required_filter_fields() -> None:
    collection = FakeCollection()
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            filter=EqualFilter("tenant_id", "tenant-a"),
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
        validate_index_before_search=False,
    )

    await provider.search("query")
    assert collection.created_search_model is None

    await provider.ensure_vector_search_index()
    assert collection.created_search_model is not None
    document = collection.created_search_model.document
    assert document["name"] == "knowledge_vector"
    assert document["type"] == "vectorSearch"
    assert document["definition"]["fields"] == [
        {
            "type": "vector",
            "path": "embedding",
            "numDimensions": 3,
            "similarity": "cosine",
        },
        {"type": "filter", "path": "tenant_id"},
    ]


async def test_parent_hydration_reapplies_authorization_and_keeps_best_child_score() -> None:
    class ParentCollection(FakeCollection):
        def __init__(self) -> None:
            super().__init__()
            self.pipelines: list[list[dict[str, Any]]] = []

        async def aggregate(self, pipeline: list[dict[str, Any]]) -> FakeCursor:
            self.pipelines.append(pipeline)
            if len(self.pipelines) == 1:
                return FakeCursor(
                    [
                        {
                            "_id": "child-1",
                            "parent_id": "parent-1",
                            "content": "child one",
                            "_ragScore": 0.7,
                        },
                        {
                            "_id": "child-2",
                            "parent_id": "parent-1",
                            "content": "child two",
                            "_ragScore": 0.9,
                        },
                    ]
                )
            return FakeCursor(
                [
                    {
                        "_id": "parent-1",
                        "content": "Authorized parent",
                        "tenant_id": "tenant-a",
                    }
                ]
            )

    collection = ParentCollection()
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            filter=EqualFilter("tenant_id", "tenant-a"),
            parent=MongoDBRAGParentOptions(max_parents=2, max_lookup_fan_out=4),
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
        validate_index_before_search=False,
    )

    results = await provider.search("query")

    assert len(results) == 1
    assert results[0].id == "parent-1"
    assert results[0].text == "Authorized parent"
    assert results[0].score == 0.9
    assert collection.pipelines[1] == [
        {
            "$match": {
                "$and": [
                    {"_id": {"$in": ["parent-1"]}},
                    {"tenant_id": {"$eq": "tenant-a"}},
                ]
            }
        },
        {"$limit": 2},
    ]


async def test_provider_closes_only_a_client_it_created(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class FakeDatabase:
        def __init__(self, collection: FakeCollection) -> None:
            self.collection = collection

        def __getitem__(self, name: str) -> FakeCollection:
            del name
            return self.collection

    class FakeClient:
        def __init__(self) -> None:
            self.collection = FakeCollection()
            self.close_calls = 0

        def __getitem__(self, name: str) -> FakeDatabase:
            del name
            return FakeDatabase(self.collection)

        async def close(self) -> None:
            self.close_calls += 1

    owned_client = FakeClient()
    handle = MongoClientHandle(owned_client, owns_client=True)

    def fake_from_uri(uri: str) -> MongoClientHandle:
        del uri
        return handle

    monkeypatch.setattr(MongoClientHandle, "from_uri", fake_from_uri)
    owned = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
    )
    injected_client = FakeClient()
    injected = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        mongo_client=injected_client,  # type: ignore[arg-type]
    )

    await owned.close()
    await owned.close()
    await injected.close()

    assert owned.owns_client is True
    assert owned_client.close_calls == 1
    assert injected.owns_client is False
    assert injected_client.close_calls == 0
