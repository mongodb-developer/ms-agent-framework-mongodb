from __future__ import annotations

import asyncio
from datetime import timedelta
from typing import Any, cast
from unittest.mock import patch

import pytest
from agent_framework import (
    AgentResponse,
    AgentSession,
    Annotation,
    Content,
    HistoryProvider,
    Message,
    SessionContext,
)
from pymongo.errors import ConnectionFailure

from agent_framework_mongodb import (
    MongoDBConfigurationError,
    MongoDBHistoryProvider,
    MongoDBHistoryProviderOptions,
    MongoDBMappingError,
    MongoDBPersistenceError,
    MongoDBRetrievalError,
)


class Result:
    def __init__(self, *, deleted_count: int = 0) -> None:
        self.deleted_count = deleted_count


class FakeCursor:
    def __init__(self, documents: list[dict[str, Any]]) -> None:
        self.documents = documents
        self.sort_call: tuple[str, int] | None = None
        self.limit_call: int | None = None

    def sort(self, field: str, direction: int) -> FakeCursor:
        self.sort_call = (field, direction)
        return self

    def limit(self, value: int) -> FakeCursor:
        self.limit_call = value
        return self

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        documents = self.documents
        if self.sort_call is not None:
            field, direction = self.sort_call
            documents = sorted(
                documents, key=lambda document: document[field], reverse=direction < 0
            )
        if self.limit_call is not None:
            documents = documents[: self.limit_call]
        if length is not None:
            documents = documents[:length]
        return documents


class FakeCollection:
    def __init__(self) -> None:
        self.documents: list[dict[str, Any]] = []
        self.sequence = 0
        self.find_filter: dict[str, Any] | None = None
        self.cursor: FakeCursor | None = None
        self.deleted_filters: list[dict[str, Any]] = []
        self.created_indexes: list[tuple[Any, dict[str, Any]]] = []
        self.fail_reads = False
        self.fail_writes = False

    async def find_one_and_update(self, *args: Any, **_kwargs: Any) -> dict[str, Any]:
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        update = cast(dict[str, dict[str, int]], args[1])
        self.sequence += update["$inc"]["sequence"]
        return {"sequence": self.sequence}

    async def insert_one(self, document: dict[str, Any]) -> Result:
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        if any(item["_id"] == document["_id"] for item in self.documents):
            from pymongo.errors import DuplicateKeyError

            raise DuplicateKeyError("duplicate")
        self.documents.append(document)
        return Result()

    def find(self, query: dict[str, Any]) -> FakeCursor:
        if self.fail_reads:
            raise ConnectionFailure("private-host.invalid")
        self.find_filter = query
        matching = [
            document
            for document in self.documents
            if all(
                document.get(key) == value
                for key, value in query.items()
                if not isinstance(value, dict)
            )
        ]
        self.cursor = FakeCursor(matching)
        return self.cursor

    async def find_one(self, query: dict[str, Any]) -> dict[str, Any] | None:
        for document in self.documents:
            if all(document.get(key) == value for key, value in query.items()):
                return document
        return None

    async def delete_many(self, query: dict[str, Any]) -> Result:
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        self.deleted_filters.append(query)
        before = len(self.documents)
        self.documents = [
            document
            for document in self.documents
            if not all(document.get(key) == value for key, value in query.items())
        ]
        return Result(deleted_count=before - len(self.documents))

    async def delete_one(self, query: dict[str, Any]) -> Result:
        self.deleted_filters.append(query)
        return Result()

    async def create_index(self, keys: Any, **kwargs: Any) -> str:
        self.created_indexes.append((keys, kwargs))
        return str(kwargs["name"])

    async def list_indexes(self) -> FakeCursor:
        return FakeCursor([])


class FakeDatabase:
    def __init__(self, collection: FakeCollection) -> None:
        self.collection = collection

    def __getitem__(self, _name: str) -> FakeCollection:
        return self.collection


class FakeClient:
    def __init__(self) -> None:
        self.collection = FakeCollection()
        self.database = FakeDatabase(self.collection)
        self.close_count = 0

    def __getitem__(self, _name: str) -> FakeDatabase:
        return self.database

    def close(self) -> None:
        self.close_count += 1


def options(**overrides: Any) -> MongoDBHistoryProviderOptions:
    values: dict[str, Any] = {
        "application_id": "app-1",
        "agent_id": "agent-1",
        "session_id": "session-1",
    }
    values.update(overrides)
    return MongoDBHistoryProviderOptions(**values)


def test_history_provider_uses_public_framework_contract() -> None:
    provider = MongoDBHistoryProvider(cast(Any, FakeCollection()), options=options())

    assert isinstance(provider, HistoryProvider)
    assert provider.source_id == "mongodb-history"
    assert provider.owns_client is False


@pytest.mark.parametrize(
    ("overrides", "message"),
    [
        ({"session_id": " "}, "session_id"),
        ({"session_id": None}, "session_id"),
        ({"application_id": None, "agent_id": None}, "authorization scope"),
        ({"max_messages": 0}, "max_messages"),
        ({"max_messages": "many"}, "max_messages"),
        ({"retention": 0}, "retention"),
        ({"retrieval_timeout": "soon"}, "retrieval_timeout"),
    ],
)
def test_options_reject_unsafe_values(overrides: dict[str, Any], message: str) -> None:
    with pytest.raises(MongoDBConfigurationError, match=message):
        options(**overrides)


async def test_messages_round_trip_losslessly_in_deterministic_order() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    supported_content_types = [
        "text",
        "text_reasoning",
        "data",
        "uri",
        "error",
        "function_call",
        "function_result",
        "usage",
        "hosted_file",
        "hosted_vector_store",
        "code_interpreter_tool_call",
        "code_interpreter_tool_result",
        "image_generation_tool_call",
        "image_generation_tool_result",
        "mcp_server_tool_call",
        "mcp_server_tool_result",
        "search_tool_call",
        "search_tool_result",
        "shell_tool_call",
        "shell_tool_result",
        "shell_command_output",
        "function_approval_request",
        "function_approval_response",
        "oauth_consent_request",
    ]
    messages = [
        Message(
            "user",
            [
                Content(type="text", text="show weather"),
                Content(
                    type="uri",
                    uri="https://example.invalid/weather.png",
                    media_type="image/png",
                    annotations=[
                        Annotation(
                            type="citation",
                            title="radar",
                            url="https://example.invalid/source",
                        )
                    ],
                    additional_properties={"content-extra": {"nested": True}},
                ),
            ],
            author_name="Ada",
            message_id="message-user",
            additional_properties={"trace": {"attempt": 2}, "flags": ["a", "b"]},
        ),
        Message(
            "assistant",
            [
                Content(
                    type="function_call",
                    call_id="call-1",
                    name="weather",
                    arguments={"city": "London"},
                )
            ],
            message_id="message-call",
        ),
        Message(
            "tool",
            [Content(type="function_result", call_id="call-1", result={"temperature": 19})],
            message_id="message-result",
        ),
        Message(
            "assistant",
            [Content(type="text", text="It is 19 C.")],
            message_id="message-answer",
        ),
        Message(
            "assistant",
            [
                Content(
                    type=cast(Any, content_type),
                    additional_properties={"fixture_type": content_type},
                )
                for content_type in supported_content_types
            ],
            message_id="message-content-contract",
        ),
    ]

    await provider.save_messages("session-1", messages)
    tied_timestamp = collection.documents[0]["created_at"]
    for document in collection.documents:
        document["created_at"] = tied_timestamp
    restored = await provider.get_messages("session-1")

    assert [message.to_dict() for message in restored] == [
        message.to_dict() for message in messages
    ]
    assert [document["sequence"] for document in collection.documents] == [1, 2, 3, 4, 5]
    assert all(document["schema_version"] == 1 for document in collection.documents)
    assert all(document["framework_version"] == 1 for document in collection.documents)


async def test_latest_n_is_queried_descending_then_returned_chronologically() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(max_messages=2, max_age=timedelta(days=7)),
    )
    await provider.save_messages(
        "session-1",
        [Message("user", [str(index)], message_id=f"m-{index}") for index in range(3)],
    )

    messages = await provider.get_messages("session-1")

    assert [message.text for message in messages] == ["1", "2"]
    assert collection.cursor is not None
    assert collection.find_filter is not None
    assert collection.cursor.sort_call == ("sequence", -1)
    assert collection.cursor.limit_call == 2
    assert collection.find_filter == {
        "_kind": "message",
        "application_id": "app-1",
        "agent_id": "agent-1",
        "session_id": "session-1",
        "created_at": collection.find_filter["created_at"],
    }
    assert "$gte" in collection.find_filter["created_at"]


async def test_batch_retry_and_duplicate_message_are_idempotent() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    messages = [
        Message("user", ["same"], message_id="stable-1"),
        Message("assistant", ["response"], message_id="stable-2"),
    ]

    await provider.save_messages("session-1", messages)
    await provider.save_messages("session-1", messages)

    assert len(collection.documents) == 2
    assert [message.text for message in await provider.get_messages("session-1")] == [
        "same",
        "response",
    ]


async def test_messages_without_ids_round_trip_exactly_and_retry_idempotently() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    message = Message("user", ["hello"])

    await provider.save_messages("session-1", [message])
    await provider.save_messages("session-1", [message])

    assert message.message_id is None
    assert len(collection.documents) == 1
    restored = await provider.get_messages("session-1")
    assert restored[0].message_id is None
    assert restored[0].to_dict() == message.to_dict()


async def test_framework_state_deduplicates_reconstructed_anonymous_batch() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    state: dict[str, Any] = {}

    await provider.save_messages("session-1", [Message("user", ["hello"])], state=state)
    await provider.save_messages("session-1", [Message("user", ["hello"])], state=state)

    assert len(collection.documents) == 1
    assert (await provider.get_messages("session-1"))[0].message_id is None


async def test_scope_mismatch_is_rejected_before_mongodb_access() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())

    with pytest.raises(MongoDBConfigurationError, match="authorized session"):
        await provider.get_messages("other-session")
    with pytest.raises(MongoDBConfigurationError, match="authorized session"):
        await provider.save_messages("other-session", [Message("user", ["no"])])

    assert collection.find_filter is None
    assert collection.documents == []


async def test_clear_messages_is_scoped_and_returns_acknowledged_count() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    await provider.save_messages(
        "session-1",
        [Message("user", ["delete"], message_id="delete-me")],
    )

    count = await provider.clear_messages("session-1")

    assert count == 1
    assert collection.deleted_filters[0] == {
        "_kind": "message",
        "application_id": "app-1",
        "agent_id": "agent-1",
        "session_id": "session-1",
    }


async def test_unknown_versions_fail_with_migration_guidance() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    await provider.save_messages(
        "session-1",
        [Message("user", ["hello"], message_id="message-1")],
    )
    collection.documents[0]["schema_version"] = 99

    with pytest.raises(MongoDBMappingError, match="migration"):
        await provider.get_messages("session-1")

    collection.documents[0]["schema_version"] = 1
    collection.documents[0]["framework_version"] = 99
    with pytest.raises(MongoDBMappingError, match="framework serialization"):
        await provider.get_messages("session-1")


async def test_regular_indexes_are_created_only_by_explicit_operation() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(retention=timedelta(days=30)),
    )

    assert collection.created_indexes == []
    names = await provider.ensure_indexes()

    assert names == (
        "history_scoped_message_unique",
        "history_scoped_sequence",
        "history_expiration_ttl",
    )
    assert collection.created_indexes[0][1]["unique"] is True
    assert collection.created_indexes[2][1]["expireAfterSeconds"] == 0


async def test_cancellation_and_stable_errors_propagate_without_sensitive_logs() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    collection.fail_reads = True
    with pytest.raises(MongoDBRetrievalError, match="History retrieval failed"):
        await provider.get_messages("session-1")
    collection.fail_reads = False
    collection.fail_writes = True
    with pytest.raises(MongoDBPersistenceError, match="History persistence failed"):
        await provider.save_messages("session-1", [Message("user", ["secret"])])

    task = asyncio.create_task(asyncio.sleep(10))
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task


async def test_base_provider_filters_inputs_context_and_outputs() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(
            store_context_messages=True,
            store_context_from=frozenset({"approved-context"}),
        ),
    )
    context = SessionContext(
        input_messages=[Message("user", ["input"], message_id="input")],
        session_id="session-1",
    )
    context.extend_messages(
        "approved-context",
        [Message("system", ["approved"], message_id="approved")],
    )
    context.extend_messages(
        "excluded-context",
        [Message("system", ["excluded"], message_id="excluded")],
    )
    context._response = AgentResponse(  # pyright: ignore[reportPrivateUsage]
        messages=[Message("assistant", ["output"], message_id="output")]
    )

    await provider.after_run(
        agent=cast(Any, object()),
        session=AgentSession(),
        context=context,
        state={},
    )
    replay_context = SessionContext(input_messages=[], session_id="session-1")
    await provider.before_run(
        agent=cast(Any, object()),
        session=AgentSession(),
        context=replay_context,
        state={},
    )

    assert [message.text for message in replay_context.get_messages()] == [
        "approved",
        "input",
        "output",
    ]
    assert replay_context.get_messages(sources={"mongodb-history"}) == replay_context.get_messages()


async def test_concurrent_batches_receive_unique_monotonic_sequences() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())

    await asyncio.gather(
        provider.save_messages(
            "session-1",
            [Message("user", ["a"], message_id="a"), Message("assistant", ["b"], message_id="b")],
        ),
        provider.save_messages(
            "session-1",
            [Message("user", ["c"], message_id="c"), Message("assistant", ["d"], message_id="d")],
        ),
    )

    sequences = [document["sequence"] for document in collection.documents]
    assert sorted(sequences) == [1, 2, 3, 4]
    assert len(set(sequences)) == 4


async def test_client_ownership_is_fixed_at_construction() -> None:
    injected = FakeClient()
    injected_provider = MongoDBHistoryProvider(
        options=options(),
        mongo_client=cast(Any, injected),
    )
    await injected_provider.close()
    assert injected_provider.owns_client is False
    assert injected.close_count == 0

    owned = FakeClient()
    with patch(
        "agent_framework_mongodb._shared.client.AsyncMongoClient",
        return_value=owned,
    ):
        owned_provider = MongoDBHistoryProvider(
            options=options(),
            connection_string="mongodb://example.invalid",
        )
    await owned_provider.close()
    await owned_provider.close()
    assert owned_provider.owns_client is True
    assert owned.close_count == 1


async def test_service_managed_history_is_rejected_before_duplicate_replay() -> None:
    provider = MongoDBHistoryProvider(cast(Any, FakeCollection()), options=options())
    context = SessionContext(
        input_messages=[],
        session_id="session-1",
        service_session_id="service-session",
    )

    with pytest.raises(MongoDBConfigurationError, match="service-managed"):
        await provider.before_run(
            agent=cast(Any, object()),
            session=AgentSession(),
            context=context,
            state={},
        )
