from __future__ import annotations

import asyncio
import json
from collections.abc import Awaitable, Sequence
from datetime import datetime, timedelta, timezone
from typing import Any, cast

import pytest
from agent_framework import (
    AgentResponse,
    AgentSession,
    Embedding,
    GeneratedEmbeddings,
    Message,
    SessionContext,
)
from pymongo.errors import ConnectionFailure

from agent_framework_mongodb import (
    MemoryMetadataPage,
    MongoDBConfigurationError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBMemoryContextProvider,
    MongoDBPersistenceError,
    MongoDBRetrievalError,
    MongoDBTimeoutError,
)


class FakeEmbeddingGenerator:
    additional_properties: dict[str, Any] = {}

    def __init__(self) -> None:
        self.calls: list[list[str]] = []
        self.cancel = False
        self.delay = 0.0

    async def _generate(self, values: Sequence[str]) -> GeneratedEmbeddings[list[float], Any]:
        if self.cancel:
            raise asyncio.CancelledError
        if self.delay:
            await asyncio.sleep(self.delay)
        self.calls.append(list(values))
        return GeneratedEmbeddings(
            [Embedding(vector=[float(index + 1), 0.0, 1.0]) for index, _ in enumerate(values)]
        )

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
        self.sort_args: tuple[str, int] | None = None
        self.limit_value: int | None = None

    def sort(self, field: str, direction: int) -> FakeCursor:
        self.sort_args = (field, direction)
        return self

    def limit(self, value: int) -> FakeCursor:
        self.limit_value = value
        return self

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        return self.documents if length is None else self.documents[:length]


class Result:
    def __init__(
        self,
        *,
        inserted_ids: list[str] | None = None,
        deleted_count: int = 0,
    ) -> None:
        self.inserted_ids = inserted_ids or []
        self.deleted_count = deleted_count


class FakeCollection:
    def __init__(self) -> None:
        self.aggregate_documents: list[dict[str, Any]] = []
        self.aggregate_pipeline: list[dict[str, Any]] | None = None
        self.inserted: list[dict[str, Any]] = []
        self.insert_attempts: list[list[dict[str, Any]]] = []
        self.deleted_filter: dict[str, Any] | None = None
        self.metadata_documents: list[dict[str, Any]] = []
        self.find_filter: dict[str, Any] | None = None
        self.find_projection: dict[str, Any] | None = None
        self.search_indexes: list[dict[str, Any]] = []
        self.created_search_model: Any | None = None
        self.created_indexes: list[tuple[Any, dict[str, Any]]] = []
        self.regular_indexes: list[dict[str, Any]] = []
        self.fail_reads = False
        self.fail_writes = False

    async def aggregate(self, pipeline: list[dict[str, Any]]) -> FakeCursor:
        if self.fail_reads:
            raise ConnectionFailure("sensitive-host.invalid")
        self.aggregate_pipeline = pipeline
        return FakeCursor(self.aggregate_documents)

    async def insert_many(self, documents: list[dict[str, Any]], *, ordered: bool) -> Result:
        assert ordered is False
        self.insert_attempts.append(documents)
        if self.fail_writes:
            raise ConnectionFailure("sensitive-host.invalid")
        self.inserted = documents
        return Result(inserted_ids=[str(document["_id"]) for document in documents])

    async def delete_many(self, query: dict[str, Any]) -> Result:
        if self.fail_writes:
            raise ConnectionFailure("sensitive-host.invalid")
        self.deleted_filter = query
        return Result(deleted_count=2)

    def find(self, query: dict[str, Any], projection: dict[str, Any]) -> FakeCursor:
        self.find_filter = query
        self.find_projection = projection
        return FakeCursor(self.metadata_documents)

    async def list_search_indexes(self, *, name: str) -> FakeCursor:
        del name
        if self.fail_reads:
            raise ConnectionFailure("sensitive-host.invalid")
        return FakeCursor(self.search_indexes)

    async def create_search_index(self, model: Any) -> str:
        self.created_search_model = model
        return "agent_framework_memory"

    async def create_index(self, keys: Any, **kwargs: Any) -> str:
        self.created_indexes.append((keys, kwargs))
        return str(kwargs["name"])

    async def list_indexes(self) -> FakeCursor:
        return FakeCursor(self.regular_indexes)


class ConcurrentInsertCollection(FakeCollection):
    def __init__(self) -> None:
        super().__init__()
        self.both_inserts_entered = asyncio.Event()
        self.release_inserts = asyncio.Event()

    async def insert_many(self, documents: list[dict[str, Any]], *, ordered: bool) -> Result:
        assert ordered is False
        self.insert_attempts.append(documents)
        if len(self.insert_attempts) == 2:
            self.both_inserts_entered.set()
        await self.release_inserts.wait()
        return Result(inserted_ids=[str(document["_id"]) for document in documents])


class BlockingInsertCollection(FakeCollection):
    def __init__(self) -> None:
        super().__init__()
        self.insert_entered = asyncio.Event()
        self.release_insert = asyncio.Event()

    async def insert_many(self, documents: list[dict[str, Any]], *, ordered: bool) -> Result:
        assert ordered is False
        self.insert_attempts.append(documents)
        self.insert_entered.set()
        await self.release_insert.wait()
        return Result(inserted_ids=[str(document["_id"]) for document in documents])


def provider(
    collection: FakeCollection,
    embeddings: FakeEmbeddingGenerator | None = None,
    **kwargs: Any,
) -> MongoDBMemoryContextProvider:
    options: dict[str, Any] = {
        "vector_dimensions": 3,
        "application_id": "app-1",
        "user_id": "user-1",
        "collection": cast(Any, collection),
    }
    options.update(kwargs)
    return MongoDBMemoryContextProvider(
        embeddings or FakeEmbeddingGenerator(),
        **options,
    )


async def test_search_builds_scoped_ann_and_optional_session_exact_pipelines() -> None:
    collection = FakeCollection()
    collection.aggregate_documents = [
        {
            "_id": "memory-1",
            "role": "user",
            "content": "remember blue",
            "session_id": "old-session",
            "created_at": datetime.now(timezone.utc),
        }
    ]
    memory = provider(collection)

    results = await memory.search("blue")

    assert results[0].text == "remember blue"
    assert collection.aggregate_pipeline is not None
    stage = collection.aggregate_pipeline[0]["$vectorSearch"]
    assert stage["filter"] == {"application_id": "app-1", "user_id": "user-1"}
    assert stage["numCandidates"] == 30
    assert "exact" not in stage

    await memory.search("blue", session_id="session-2", exact=True)
    stage = collection.aggregate_pipeline[0]["$vectorSearch"]
    assert stage["filter"]["session_id"] == "session-2"
    assert stage["exact"] is True
    assert "numCandidates" not in stage


async def test_store_batches_embeddings_and_uses_stable_message_ids() -> None:
    collection = FakeCollection()
    embeddings = FakeEmbeddingGenerator()
    memory = provider(collection, embeddings, retention=timedelta(days=7))
    messages = [
        Message("user", ["first"], message_id="message-1"),
        Message("assistant", ["second"]),
        Message("tool", ["ignored"]),
        Message(
            "system",
            ["injected"],
            additional_properties={"_attribution": {"source_id": "another-provider"}},
        ),
    ]

    assert await memory.store(messages, session_id="session-1") == 2
    first_ids = [document["_id"] for document in collection.inserted]
    assert embeddings.calls == [["first", "second"]]
    assert all(document["session_id"] == "session-1" for document in collection.inserted)
    assert all("expires_at" in document for document in collection.inserted)

    await memory.store(messages, session_id="session-1")
    later_ids = [document["_id"] for document in collection.inserted]
    assert later_ids[0] == first_ids[0]
    assert later_ids[1] != first_ids[1]


async def test_no_message_id_reuses_pending_retry_id_then_advances_after_success() -> None:
    collection = FakeCollection()
    memory = provider(collection)
    state: dict[str, Any] = {}
    messages = [Message("user", ["identical content"])]
    collection.fail_writes = True

    with pytest.raises(MongoDBPersistenceError):
        await memory.store(messages, session_id="session-1", state=state)
    failed_id = collection.insert_attempts[0][0]["_id"]

    collection.fail_writes = False
    await memory.store(messages, session_id="session-1", state=state)
    retry_id = collection.insert_attempts[1][0]["_id"]
    await memory.store(messages, session_id="session-1", state=state)
    later_run_id = collection.insert_attempts[2][0]["_id"]

    assert retry_id == failed_id
    assert later_run_id != retry_id


async def test_concurrent_identical_batches_receive_distinct_fallback_ids() -> None:
    collection = ConcurrentInsertCollection()
    memory = provider(collection)
    state: dict[str, Any] = {}
    messages = [Message("user", ["identical concurrent content"])]

    first = asyncio.create_task(memory.store(messages, session_id="session-1", state=state))
    second = asyncio.create_task(memory.store(messages, session_id="session-1", state=state))
    await asyncio.wait_for(collection.both_inserts_entered.wait(), timeout=1)
    attempted_ids = [attempt[0]["_id"] for attempt in collection.insert_attempts]
    json.dumps(state)
    collection.release_inserts.set()

    assert await asyncio.gather(first, second) == [1, 1]
    assert attempted_ids[0] != attempted_ids[1]
    assert state == {}


async def test_cancelled_store_preserves_fallback_ids_for_retry() -> None:
    collection = BlockingInsertCollection()
    memory = provider(collection)
    state: dict[str, Any] = {}
    messages = [Message("user", ["cancelled pending content"])]

    pending = asyncio.create_task(memory.store(messages, session_id="session-1", state=state))
    await asyncio.wait_for(collection.insert_entered.wait(), timeout=1)
    cancelled_id = collection.insert_attempts[0][0]["_id"]
    pending.cancel()
    with pytest.raises(asyncio.CancelledError):
        await pending

    assert state
    json.dumps(state)
    collection.release_insert.set()
    await memory.store(messages, session_id="session-1", state=state)

    assert collection.insert_attempts[1][0]["_id"] == cancelled_id
    assert state == {}


async def test_direct_failures_surface_stable_errors_with_driver_causes() -> None:
    collection = FakeCollection()
    collection.fail_reads = True
    memory = provider(collection)

    with pytest.raises(MongoDBRetrievalError) as error:
        await memory.search("query")

    assert isinstance(error.value.__cause__, ConnectionFailure)

    collection.fail_reads = False
    collection.fail_writes = True
    with pytest.raises(MongoDBPersistenceError) as error:
        await memory.store([Message("user", ["content"])])
    assert isinstance(error.value.__cause__, ConnectionFailure)


async def test_hooks_fail_open_for_operations_but_propagate_cancellation(
    caplog: pytest.LogCaptureFixture,
) -> None:
    collection = FakeCollection()
    collection.fail_reads = True
    memory = provider(collection)
    context = SessionContext(input_messages=[Message("user", ["secret query"])])

    await memory.before_run(
        agent=object(),
        session=AgentSession(),
        context=context,
        state={},
    )

    assert context.context_messages == {}
    assert "secret query" not in caplog.text
    assert "sensitive-host" not in caplog.text

    embeddings = FakeEmbeddingGenerator()
    embeddings.cancel = True
    cancelling_memory = provider(FakeCollection(), embeddings)
    with pytest.raises(asyncio.CancelledError):
        await cancelling_memory.before_run(
            agent=object(),
            session=AgentSession(),
            context=context,
            state={},
        )


async def test_before_run_injects_attributed_cross_session_memory() -> None:
    collection = FakeCollection()
    collection.aggregate_documents = [
        {
            "_id": "memory-1",
            "role": "assistant",
            "content": "remembered response",
            "session_id": "origin-session",
        }
    ]
    memory = provider(collection)
    context = SessionContext(input_messages=[Message("user", ["question"])])

    await memory.before_run(
        agent=object(),
        session=AgentSession(),
        context=context,
        state={},
    )

    injected = context.context_messages[memory.source_id][0]
    assert injected.text == "remembered response"
    assert injected.additional_properties["_attribution"] == {
        "source_id": memory.source_id,
        "source_type": "MongoDBMemoryContextProvider",
        "origin_session_ids": ["origin-session"],
    }
    assert context.instructions == [memory.context_prompt]


async def test_after_run_default_fail_open_and_configurable_fail_fast() -> None:
    collection = FakeCollection()
    collection.fail_writes = True
    context = SessionContext(
        session_id="session-1",
        input_messages=[Message("user", ["input"])],
    )
    cast(Any, context)._response = AgentResponse(messages=[Message("assistant", ["response"])])

    await provider(collection).after_run(
        agent=object(), session=AgentSession(), context=context, state={}
    )

    with pytest.raises(MongoDBPersistenceError):
        await provider(collection, persistence_fail_fast=True).after_run(
            agent=object(), session=AgentSession(), context=context, state={}
        )


async def test_after_run_stores_only_input_and_response_text() -> None:
    collection = FakeCollection()
    memory = provider(collection)
    context = SessionContext(
        session_id="session-1",
        input_messages=[Message("user", ["input"], message_id="input-1")],
        context_messages={
            memory.source_id: [
                Message(
                    "assistant",
                    ["memory context"],
                    additional_properties={"_attribution": {"source_id": memory.source_id}},
                )
            ]
        },
    )
    cast(Any, context)._response = AgentResponse(
        messages=[Message("assistant", ["response"], message_id="response-1")]
    )

    await memory.after_run(agent=object(), session=AgentSession(), context=context, state={})

    assert [document["content"] for document in collection.inserted] == [
        "input",
        "response",
    ]


async def test_scoped_deletion_and_bounded_metadata_pagination() -> None:
    collection = FakeCollection()
    now = datetime.now(timezone.utc)
    collection.metadata_documents = [
        {"_id": "a", "role": "user", "created_at": now, "user_id": "user-1"},
        {"_id": "b", "role": "assistant", "created_at": now, "user_id": "user-1"},
    ]
    memory = provider(collection)

    assert await memory.delete_memory("memory-1") == 2
    assert collection.deleted_filter == {
        "_id": "memory-1",
        "application_id": "app-1",
        "user_id": "user-1",
    }
    await memory.clear_session("session-1")
    assert collection.deleted_filter is not None
    assert collection.deleted_filter["session_id"] == "session-1"
    await memory.clear_user()
    assert collection.deleted_filter == {"application_id": "app-1", "user_id": "user-1"}

    page = await memory.list_metadata(page_size=1)
    assert isinstance(page, MemoryMetadataPage)
    assert [item.memory_id for item in page.items] == ["a"]
    assert page.next_cursor == "a"
    assert collection.find_projection is not None
    assert "content" not in collection.find_projection

    with pytest.raises(MongoDBConfigurationError, match="page_size"):
        await memory.list_metadata(page_size=101)


async def test_explicit_search_and_regular_index_operations_remain_separate() -> None:
    collection = FakeCollection()
    memory = provider(collection, retention=timedelta(days=1))

    await memory.create_vector_search_index()
    assert collection.created_search_model is not None
    assert collection.created_indexes == []

    regular_names = await memory.ensure_regular_indexes()
    assert regular_names == ("memory_scope_admin", "memory_expiration_ttl")
    assert collection.created_indexes[1][1]["expireAfterSeconds"] == 0
    collection.regular_indexes = [
        {
            "name": "memory_scope_admin",
            "key": {
                "application_id": 1,
                "agent_id": 1,
                "user_id": 1,
                "session_id": 1,
                "_id": 1,
            },
        },
        {
            "name": "memory_expiration_ttl",
            "key": {"expires_at": 1},
            "expireAfterSeconds": 0,
        },
    ]
    await memory.validate_regular_indexes()

    with pytest.raises(MongoDBIndexMissingError):
        await memory.validate_vector_search_index()

    collection.search_indexes = [
        {
            "name": "agent_framework_memory",
            "status": "READY",
            "queryable": True,
            "latestDefinition": {
                "fields": [
                    {
                        "type": "vector",
                        "path": "wrong",
                        "numDimensions": 3,
                        "similarity": "cosine",
                    }
                ]
            },
        }
    ]
    with pytest.raises(MongoDBIndexMismatchError):
        await memory.validate_vector_search_index()

    collection.search_indexes[0]["latestDefinition"] = {
        "fields": [
            {
                "type": "vector",
                "path": "content_embedding",
                "numDimensions": 3,
                "similarity": "cosine",
            },
            *[
                {"type": "filter", "path": field}
                for field in ("application_id", "agent_id", "user_id", "session_id")
            ],
        ]
    }
    await memory.validate_vector_search_index()


def test_options_are_bounded_and_no_operation_provisions_indexes() -> None:
    collection = FakeCollection()
    with pytest.raises(MongoDBConfigurationError, match="num_candidates"):
        provider(collection, max_results=5, num_candidates=4)
    with pytest.raises(MongoDBConfigurationError, match="similarity"):
        provider(collection, similarity="invalid")
    with pytest.raises(MongoDBConfigurationError, match="retention"):
        provider(collection, retention=timedelta(0))
    with pytest.raises(MongoDBConfigurationError, match="retrieval_timeout"):
        provider(collection, retrieval_timeout=0)
    assert collection.created_search_model is None


@pytest.mark.parametrize(
    ("option_name", "value"),
    [
        ("vector_dimensions", 3.0),
        ("vector_dimensions", True),
        ("max_results", 3.0),
        ("max_results", True),
        ("num_candidates", 30.0),
        ("num_candidates", True),
    ],
)
def test_constructor_integer_limits_reject_non_integers(
    option_name: str,
    value: object,
) -> None:
    with pytest.raises(MongoDBConfigurationError, match=option_name):
        if option_name == "vector_dimensions":
            provider(FakeCollection(), vector_dimensions=cast(Any, value))
        elif option_name == "max_results":
            provider(FakeCollection(), max_results=cast(Any, value))
        else:
            provider(FakeCollection(), num_candidates=cast(Any, value))


@pytest.mark.parametrize("value", [1.5, True])
async def test_operation_integer_limits_reject_non_integers(value: object) -> None:
    memory = provider(FakeCollection())

    with pytest.raises(MongoDBConfigurationError, match="max_results"):
        await memory.search("query", max_results=cast(Any, value))
    with pytest.raises(MongoDBConfigurationError, match="page_size"):
        await memory.list_metadata(page_size=cast(Any, value))


async def test_clear_user_requires_application_or_agent_authorization_scope() -> None:
    collection = FakeCollection()
    memory = MongoDBMemoryContextProvider(
        FakeEmbeddingGenerator(),
        vector_dimensions=3,
        user_id="user-1",
        collection=cast(Any, collection),
    )

    with pytest.raises(MongoDBConfigurationError, match="application_id or agent_id"):
        await memory.clear_user()


async def test_direct_operation_deadlines_surface_stable_timeout_error() -> None:
    embeddings = FakeEmbeddingGenerator()
    embeddings.delay = 0.05
    memory = provider(FakeCollection(), embeddings, retrieval_timeout=0.001)

    with pytest.raises(MongoDBTimeoutError):
        await memory.search("query")
