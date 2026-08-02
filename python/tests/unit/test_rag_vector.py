from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Sequence
from dataclasses import dataclass
from typing import Any

import pytest
from agent_framework import AgentSession, Embedding, GeneratedEmbeddings, Message, SessionContext
from pymongo.errors import OperationFailure

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBAuthorizationError,
    MongoDBCapabilityError,
    MongoDBFilter,
    MongoDBFilterTranslationError,
    MongoDBIndexFailedError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBIndexNotReadyError,
    MongoDBRAGContextProvider,
    MongoDBRAGParentOptions,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBRAGSearchOptions,
    MongoDBRetrievalError,
    MongoDBSearchMode,
    MongoDBTransientRetrievalError,
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
        self.search_indexes: list[dict[str, Any]] = [
            {
                "name": "knowledge_vector",
                "type": "vectorSearch",
                "status": "READY",
                "queryable": True,
                "latestDefinition": {
                    "fields": [
                        {
                            "type": "vector",
                            "path": "embedding",
                            "numDimensions": 3,
                            "similarity": "cosine",
                        },
                        {"type": "filter", "path": "tenant_id"},
                        {"type": "filter", "path": "metadata.kind"},
                        {"type": "filter", "path": "record_type"},
                    ]
                },
            }
        ]
        self.read_error: Exception | None = None
        self.created_search_model: Any | None = None
        self.updated_search_definition: tuple[str, dict[str, Any]] | None = None
        self.database = CapabilityDatabase()
        self.aggregate_calls = 0
        self.index_reads = 0

    async def aggregate(self, pipeline: list[dict[str, Any]]) -> FakeCursor:
        self.aggregate_calls += 1
        if self.read_error is not None:
            raise self.read_error
        self.pipeline = pipeline
        return FakeCursor(self.documents)

    async def list_search_indexes(self, *, name: str) -> FakeCursor:
        self.index_reads += 1
        return FakeCursor([index for index in self.search_indexes if index.get("name") == name])

    async def create_search_index(self, model: Any) -> str:
        self.created_search_model = model
        document = model.document
        self.search_indexes = [
            {
                "name": document["name"],
                "type": document["type"],
                "status": "BUILDING",
                "queryable": False,
                "latestDefinition": document["definition"],
            }
        ]
        return "knowledge_vector"

    async def update_search_index(self, name: str, definition: dict[str, Any]) -> None:
        self.updated_search_definition = (name, definition)


class CapabilityDatabase:
    def __init__(self) -> None:
        self.command_calls: list[dict[str, Any] | str] = []
        self.command_error: BaseException | None = None
        self.explain_error: BaseException | None = None

    async def command(self, command: dict[str, Any] | str) -> dict[str, Any]:
        self.command_calls.append(command)
        if isinstance(command, dict) and "explain" in command and self.explain_error is not None:
            raise self.explain_error
        if self.command_error is not None:
            raise self.command_error
        if command == "buildInfo":
            return {"version": "test-server"}
        if command == "hello":
            return {"msg": "isdbgrid"}
        return {"ok": 1}


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


async def test_search_validates_per_call_filter_paths_before_embedding() -> None:
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
                        "path": "embedding",
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

    with pytest.raises(MongoDBIndexMismatchError, match="filter paths"):
        await provider.search(
            "query",
            options=MongoDBRAGSearchOptions(filter=EqualFilter("metadata.kind", "reference")),
        )

    assert embeddings.calls == []
    assert collection.aggregate_calls == 0


async def test_search_rejects_incomplete_filter_translation_before_io() -> None:
    @dataclass(frozen=True, slots=True)
    class UnsupportedFilter(MongoDBFilter):
        pass

    collection = FakeCollection()
    embeddings = FakeEmbeddingGenerator()
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=embeddings,
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBFilterTranslationError, match="unsupported"):
        await provider.search(
            "query",
            options=MongoDBRAGSearchOptions(filter=UnsupportedFilter()),
        )

    assert collection.index_reads == 0
    assert embeddings.calls == []
    assert collection.aggregate_calls == 0


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


async def test_enn_capability_failure_precedes_embedding_and_retrieval() -> None:
    collection = FakeCollection()
    collection.database.explain_error = OperationFailure(
        "exact vector mode is unavailable",
        code=40324,
        details={"codeName": "Location40324"},
    )
    embeddings = FakeEmbeddingGenerator()
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ENN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=embeddings,
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBCapabilityError, match="exact.*unavailable.*remediation"):
        await provider.search("exact query")

    assert embeddings.calls == []
    assert collection.aggregate_calls == 0


async def test_enn_capability_facts_are_cached_across_searches() -> None:
    collection = FakeCollection()
    collection.documents = [{"_id": "doc-1", "content": "Exact", "_ragScore": 1.0}]
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ENN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    await provider.search("first")
    await provider.search("second")

    assert collection.database.command_calls == [
        "buildInfo",
        "hello",
        {
            "explain": {
                "aggregate": "knowledge",
                "pipeline": [
                    {
                        "$vectorSearch": {
                            "index": "knowledge_vector",
                            "path": "embedding",
                            "queryVector": [1.0, 0.0, 0.0],
                            "exact": True,
                            "limit": 1,
                        }
                    }
                ],
                "cursor": {},
            },
            "verbosity": "queryPlanner",
        },
    ]
    assert collection.aggregate_calls == 2


@pytest.mark.parametrize(
    ("command_error", "expected_error"),
    [
        (OperationFailure("forbidden", code=13), MongoDBAuthorizationError),
        (asyncio.CancelledError(), asyncio.CancelledError),
    ],
)
async def test_enn_capability_auth_and_cancellation_propagate(
    command_error: BaseException,
    expected_error: type[BaseException],
) -> None:
    collection = FakeCollection()
    collection.database.command_error = command_error
    embeddings = FakeEmbeddingGenerator()
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ENN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=embeddings,
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(expected_error):
        await provider.search("exact")

    assert embeddings.calls == []
    assert collection.aggregate_calls == 0


@pytest.mark.parametrize(
    ("probe_error", "expected_error"),
    [
        (
            OperationFailure(
                "interrupted",
                code=11601,
                details={"codeName": "Interrupted"},
            ),
            MongoDBTransientRetrievalError,
        ),
        (OperationFailure("unknown probe failure", code=8), MongoDBRetrievalError),
    ],
)
async def test_enn_probe_operational_failures_do_not_poison_cache(
    probe_error: OperationFailure,
    expected_error: type[Exception],
) -> None:
    collection = FakeCollection()
    collection.documents = [{"_id": "doc-1", "content": "Exact", "_ragScore": 1.0}]
    collection.database.explain_error = probe_error
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ENN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(expected_error):
        await provider.search("first")

    collection.database.explain_error = None
    results = await provider.search("second")

    assert [result.id for result in results] == ["doc-1"]
    assert (
        sum(
            isinstance(command, dict) and "explain" in command
            for command in collection.database.command_calls
        )
        == 2
    )


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
    collection.search_indexes = []
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            filter=EqualFilter("tenant_id", "tenant-a"),
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBIndexMissingError):
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


@pytest.mark.parametrize(
    ("index", "expected_error"),
    [
        (None, MongoDBIndexMissingError),
        (
            {
                "name": "knowledge_vector",
                "type": "vectorSearch",
                "status": "BUILDING",
                "queryable": False,
            },
            MongoDBIndexNotReadyError,
        ),
        (
            {
                "name": "knowledge_vector",
                "type": "vectorSearch",
                "status": "FAILED",
                "queryable": False,
            },
            MongoDBIndexFailedError,
        ),
    ],
)
async def test_index_facade_distinguishes_missing_building_and_failed(
    index: dict[str, Any] | None,
    expected_error: type[Exception],
) -> None:
    collection = FakeCollection()
    if index is not None:
        if index["status"] != "FAILED":
            index["latestDefinition"] = {
                "fields": [
                    {
                        "type": "vector",
                        "path": "embedding",
                        "numDimensions": 3,
                        "similarity": "cosine",
                    }
                ]
            }
        collection.search_indexes = [index]
    else:
        collection.search_indexes = []
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(expected_error):
        await provider.validate_vector_search_index()


async def test_ready_index_validates_successfully() -> None:
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
                        "path": "embedding",
                        "numDimensions": 3,
                        "similarity": "cosine",
                    }
                ]
            },
        }
    ]
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    await provider.validate_vector_search_index()


async def test_index_readiness_polling_stops_immediately_on_failed_state() -> None:
    class TransitioningCollection(FakeCollection):
        def __init__(self) -> None:
            super().__init__()
            self.index_reads = 0

        async def list_search_indexes(self, *, name: str) -> FakeCursor:
            self.index_reads += 1
            status = "BUILDING" if self.index_reads == 1 else "FAILED"
            return FakeCursor(
                [
                    {
                        "name": name,
                        "type": "vectorSearch",
                        "status": status,
                        "queryable": False,
                        "latestDefinition": {
                            "fields": [
                                {
                                    "type": "vector",
                                    "path": "embedding",
                                    "numDimensions": 3,
                                    "similarity": "cosine",
                                }
                            ]
                        },
                    }
                ]
            )

    collection = TransitioningCollection()
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBIndexFailedError, match="FAILED.*remediation"):
        await provider.ensure_vector_search_index(
            wait_until_ready=True,
            timeout=10,
            poll_interval=0.001,
        )

    assert collection.index_reads == 2


@pytest.mark.parametrize(
    ("status", "expected_error"),
    [
        ("BUILDING", None),
        ("FAILED", MongoDBIndexFailedError),
    ],
)
async def test_non_waiting_ensure_validates_building_and_failed_states(
    status: str,
    expected_error: type[Exception] | None,
) -> None:
    collection = FakeCollection()
    index: dict[str, Any] = {
        "name": "knowledge_vector",
        "type": "vectorSearch",
        "status": status,
        "queryable": False,
    }
    if status != "FAILED":
        index["latestDefinition"] = {
            "fields": [
                {
                    "type": "vector",
                    "path": "embedding",
                    "numDimensions": 3,
                    "similarity": "cosine",
                }
            ]
        }
    collection.search_indexes = [index]
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    if expected_error is None:
        await provider.ensure_vector_search_index(wait_until_ready=False)
    else:
        with pytest.raises(expected_error, match="FAILED.*remediation"):
            await provider.ensure_vector_search_index(wait_until_ready=False)

    assert collection.updated_search_definition is None


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
    )

    results = await provider.search("query")

    assert len(results) == 1
    assert results[0].id == "parent-1"
    assert results[0].text == "Authorized parent"
    assert results[0].score == 0.9
    assert collection.pipelines[0][0]["$vectorSearch"]["filter"] == {
        "$and": [
            {"tenant_id": {"$eq": "tenant-a"}},
            {"record_type": {"$eq": "child"}},
        ]
    }
    assert collection.pipelines[1] == [
        {
            "$match": {
                "$and": [
                    {"_id": {"$in": ["parent-1"]}},
                    {"tenant_id": {"$eq": "tenant-a"}},
                ]
            }
        }
    ]


async def test_parent_hydration_ranks_before_limiting_unordered_documents() -> None:
    class UnorderedParentCollection(FakeCollection):
        def __init__(self) -> None:
            super().__init__()
            self.calls = 0

        async def aggregate(self, pipeline: list[dict[str, Any]]) -> FakeCursor:
            del pipeline
            self.calls += 1
            if self.calls == 1:
                return FakeCursor(
                    [
                        {
                            "_id": f"child-{rank}",
                            "parent_id": f"parent-{rank}",
                            "content": "child",
                            "_ragScore": score,
                        }
                        for rank, score in ((1, 0.9), (2, 0.8), (3, 0.7))
                    ]
                )
            return FakeCursor(
                [
                    {"_id": "parent-3", "content": "third"},
                    {"_id": "parent-2", "content": "second"},
                    {"_id": "parent-1", "content": "first"},
                ]
            )

    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            parent=MongoDBRAGParentOptions(max_parents=2, max_lookup_fan_out=3),
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=UnorderedParentCollection(),  # type: ignore[arg-type]
    )

    results = await provider.search("query")

    assert [(result.id, result.score) for result in results] == [
        ("parent-1", 0.9),
        ("parent-2", 0.8),
    ]


async def test_parent_hydration_uses_authorization_not_child_relevance_filter() -> None:
    class ChildMetadataCollection(FakeCollection):
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
                            "content": "matching child",
                            "metadata": {"kind": "child-only"},
                            "tenant_id": "tenant-a",
                            "_ragScore": 0.9,
                        }
                    ]
                )
            return FakeCursor(
                [
                    {
                        "_id": "parent-1",
                        "content": "authorized parent",
                        "tenant_id": "tenant-a",
                    }
                ]
            )

    collection = ChildMetadataCollection()
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            filter=EqualFilter("tenant_id", "tenant-a"),
            parent=MongoDBRAGParentOptions(),
        ),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    results = await provider.search(
        "query",
        options=MongoDBRAGSearchOptions(filter=EqualFilter("metadata.kind", "child-only")),
    )

    assert results[0].text == "authorized parent"
    assert collection.pipelines[0][0]["$vectorSearch"]["filter"] == {
        "$and": [
            {
                "$and": [
                    {"tenant_id": {"$eq": "tenant-a"}},
                    {"metadata.kind": {"$eq": "child-only"}},
                ]
            },
            {"record_type": {"$eq": "child"}},
        ]
    }
    assert collection.pipelines[1] == [
        {
            "$match": {
                "$and": [
                    {"_id": {"$in": ["parent-1"]}},
                    {"tenant_id": {"$eq": "tenant-a"}},
                ]
            }
        }
    ]


async def test_provider_closes_only_a_client_it_created(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class ClientDatabase:
        def __init__(self, collection: FakeCollection) -> None:
            self.collection = collection

        def __getitem__(self, name: str) -> FakeCollection:
            del name
            return self.collection

    class FakeClient:
        def __init__(self) -> None:
            self.collection = FakeCollection()
            self.close_calls = 0

        def __getitem__(self, name: str) -> ClientDatabase:
            del name
            return ClientDatabase(self.collection)

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
