from __future__ import annotations

import asyncio
import json
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
    MongoDBIndexMismatchError,
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
        self.regular_indexes: list[dict[str, Any]] = []
        self.counter_filters: list[dict[str, Any]] = []
        self.fail_reads = False
        self.fail_writes = False
        self.cancel_writes = False

    async def find_one_and_update(self, *args: Any, **_kwargs: Any) -> dict[str, Any]:
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        self.counter_filters.append(args[0])
        update = cast(dict[str, dict[str, int]], args[1])
        self.sequence += update["$inc"]["sequence"]
        return {"sequence": self.sequence}

    async def insert_one(self, document: dict[str, Any]) -> Result:
        if self.cancel_writes:
            raise asyncio.CancelledError
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
            if matches_query(document, query):
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
        before = len(self.documents)
        self.documents = [
            document
            for document in self.documents
            if not all(document.get(key) == value for key, value in query.items())
        ]
        return Result(deleted_count=before - len(self.documents))

    async def create_index(self, keys: Any, **kwargs: Any) -> str:
        self.created_indexes.append((keys, kwargs))
        return str(kwargs["name"])

    async def list_indexes(self) -> FakeCursor:
        return FakeCursor(self.regular_indexes)


class PartialFailureCollection(FakeCollection):
    def __init__(self) -> None:
        super().__init__()
        self.message_insert_attempt = 0

    async def insert_one(self, document: dict[str, Any]) -> Result:
        if document.get("_kind") == "message":
            self.message_insert_attempt += 1
            if self.message_insert_attempt == 2:
                raise ConnectionFailure("controlled partial failure")
        return await super().insert_one(document)


class ConcurrentAttemptCollection(FakeCollection):
    def __init__(self) -> None:
        super().__init__()
        self.message_find_count = 0
        self.both_attempts_started = asyncio.Event()

    async def find_one(self, query: dict[str, Any]) -> dict[str, Any] | None:
        if query.get("_kind") == "message" and self.message_find_count < 2:
            self.message_find_count += 1
            if self.message_find_count == 2:
                self.both_attempts_started.set()
            await self.both_attempts_started.wait()
        return await super().find_one(query)


class ExplicitOverlapCollection(FakeCollection):
    def __init__(self) -> None:
        super().__init__()
        self.message_find_count = 0
        self.both_message_reads = asyncio.Event()
        self.winner_completed = asyncio.Event()

    async def find_one(self, query: dict[str, Any]) -> dict[str, Any] | None:
        task = asyncio.current_task()
        task_name = task.get_name() if task is not None else ""
        if query.get("_kind") == "message" and self.message_find_count < 2:
            self.message_find_count += 1
            if self.message_find_count == 2:
                self.both_message_reads.set()
            await self.both_message_reads.wait()
            return None
        if query.get("_kind") == "reservation" and task_name == "loser":
            await self.winner_completed.wait()
        return await super().find_one(query)


def matches_query(document: dict[str, Any], query: dict[str, Any]) -> bool:
    for key, value in query.items():
        if key == "$and":
            if not all(matches_query(document, clause) for clause in value):
                return False
            continue
        if key == "$or":
            if not any(matches_query(document, clause) for clause in value):
                return False
            continue
        if isinstance(value, dict):
            if "$exists" in value and (key in document) is not value["$exists"]:
                return False
            if "$type" in value:
                expected_type = cast(dict[str, object], value)["$type"]
                if expected_type in (10, "null") and document.get(key, object()) is not None:
                    return False
            continue
        if document.get(key) != value:
            return False
    return True


def message_documents(collection: FakeCollection) -> list[dict[str, Any]]:
    return [document for document in collection.documents if document.get("_kind") == "message"]


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
    stored_messages = message_documents(collection)
    tied_timestamp = stored_messages[0]["created_at"]
    for document in stored_messages:
        document["created_at"] = tied_timestamp
    restored = await provider.get_messages("session-1")

    assert [message.to_dict() for message in restored] == [
        message.to_dict() for message in messages
    ]
    assert [document["sequence"] for document in stored_messages] == [1, 2, 3, 4, 5]
    assert all(document["schema_version"] == 2 for document in stored_messages)
    assert all(document["framework_version"] == 1 for document in stored_messages)


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
        "scope_discriminator": collection.find_filter["scope_discriminator"],
        "tenant_id": None,
        "application_id": "app-1",
        "agent_id": "agent-1",
        "user_id": None,
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

    assert len(message_documents(collection)) == 2
    assert [message.text for message in await provider.get_messages("session-1")] == [
        "same",
        "response",
    ]


async def test_later_direct_anonymous_turn_preserves_payload_with_new_identity() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    message = Message("user", ["hello"])

    await provider.save_messages("session-1", [message])
    await provider.save_messages("session-1", [message])

    assert message.message_id is None
    assert len(message_documents(collection)) == 2
    restored = await provider.get_messages("session-1")
    assert all(item.message_id is None for item in restored)
    assert all(item.to_dict() == message.to_dict() for item in restored)


async def test_completed_framework_state_allows_later_identical_anonymous_turn() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    state: dict[str, Any] = {}

    await provider.save_messages("session-1", [Message("user", ["hello"])], state=state)
    await provider.save_messages("session-1", [Message("user", ["hello"])], state=state)

    assert len(message_documents(collection)) == 2
    assert all(message.message_id is None for message in await provider.get_messages("session-1"))


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
    discriminator = message_documents(collection)[0]["scope_discriminator"]

    count = await provider.clear_messages("session-1")

    assert count == 1
    assert {
        "_kind": "message",
        "scope_discriminator": discriminator,
        "tenant_id": None,
        "application_id": "app-1",
        "agent_id": "agent-1",
        "user_id": None,
        "session_id": "session-1",
    } in collection.deleted_filters


async def test_unknown_versions_fail_with_migration_guidance() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    await provider.save_messages(
        "session-1",
        [Message("user", ["hello"], message_id="message-1")],
    )
    message_documents(collection)[0]["schema_version"] = 99

    with pytest.raises(MongoDBMappingError, match="migration"):
        await provider.get_messages("session-1")

    message_documents(collection)[0]["schema_version"] = 2
    message_documents(collection)[0]["framework_version"] = 99
    with pytest.raises(MongoDBMappingError, match="framework serialization"):
        await provider.get_messages("session-1")


async def test_schema_v1_exact_raw_scope_is_detected_before_empty_history() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(application_id=None),
    )
    collection.documents.extend(
        [
            {
                "_id": "other-partition-v1",
                "_kind": "message",
                "schema_version": 1,
                "tenant_id": None,
                "application_id": "other-app",
                "agent_id": "agent-1",
                "session_id": "session-1",
            },
            {
                "_id": "authorized-v1",
                "_kind": "message",
                "schema_version": 1,
                "agent_id": "agent-1",
                "session_id": "session-1",
            },
        ]
    )

    with pytest.raises(MongoDBMappingError, match="schema version 1.*migration"):
        await provider.get_messages("session-1")


async def test_schema_v1_detection_does_not_wildcard_absent_dimensions() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(application_id=None),
    )
    collection.documents.append(
        {
            "_id": "other-partition-v1",
            "_kind": "message",
            "schema_version": 1,
            "application_id": "other-app",
            "agent_id": "agent-1",
            "session_id": "session-1",
        }
    )

    assert await provider.get_messages("session-1") == []


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
        "history_reservation_ttl",
    )
    assert collection.created_indexes[0][1]["unique"] is True
    assert collection.created_indexes[2][1]["expireAfterSeconds"] == 0
    collection.regular_indexes = [
        {"key": dict(keys), **definition} for keys, definition in collection.created_indexes
    ]
    await provider.validate_indexes()


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
    collection.fail_writes = False
    collection.cancel_writes = True
    with pytest.raises(asyncio.CancelledError):
        await provider.save_messages("session-1", [Message("user", ["cancel"])])

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

    sequences = [document["sequence"] for document in message_documents(collection)]
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


async def test_absent_scope_dimensions_do_not_wildcard_more_specific_partition() -> None:
    collection = FakeCollection()
    specific = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(application_id="specific-app"),
    )
    less_specific = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(application_id=None),
    )
    await specific.save_messages(
        "session-1",
        [Message("user", ["specific"], message_id="specific-message")],
    )

    assert await less_specific.get_messages("session-1") == []
    assert await less_specific.clear_messages("session-1") == 0
    assert [message.text for message in await specific.get_messages("session-1")] == ["specific"]


async def test_scope_discriminator_is_required_at_every_mongodb_boundary() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(application_id=None, user_id="user-1"),
    )

    await provider.save_messages(
        "session-1",
        [Message("user", ["scoped"], message_id="scope-message")],
    )
    stored = dict(message_documents(collection)[0])
    await provider.get_messages("session-1")
    await provider.clear_messages("session-1")
    await provider.ensure_indexes()

    discriminator = stored["scope_discriminator"]
    assert isinstance(discriminator, str)
    assert stored["application_id"] is None
    assert stored["tenant_id"] is None
    assert stored["user_id"] == "user-1"
    assert all(
        query["scope_discriminator"] == discriminator for query in collection.counter_filters
    )
    assert collection.find_filter is not None
    assert collection.find_filter["scope_discriminator"] == discriminator
    assert all(
        query["scope_discriminator"] == discriminator for query in collection.deleted_filters
    )
    assert all(
        keys[0] == ("scope_discriminator", 1) for keys, _options in collection.created_indexes[:2]
    )
    assert all(
        definition["partialFilterExpression"]["scope_discriminator"] == {"$type": "string"}
        for _keys, definition in collection.created_indexes
    )


async def test_later_identical_anonymous_turn_gets_new_identity_after_success() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    state: dict[str, Any] = {}

    await provider.save_messages("session-1", [Message("user", ["same"])], state=state)
    await provider.save_messages("session-1", [Message("user", ["same"])], state=state)

    stored_messages = message_documents(collection)
    assert len(stored_messages) == 2
    assert stored_messages[0]["stable_message_id"] != stored_messages[1]["stable_message_id"]


async def test_concurrent_identical_anonymous_attempts_do_not_share_identity() -> None:
    collection = ConcurrentAttemptCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    state: dict[str, Any] = {}

    await asyncio.gather(
        provider.save_messages("session-1", [Message("user", ["same"])], state=state),
        provider.save_messages("session-1", [Message("user", ["same"])], state=state),
    )

    stored_messages = message_documents(collection)
    assert len(stored_messages) == 2
    assert len({document["stable_message_id"] for document in stored_messages}) == 2


async def test_concurrent_explicit_id_loser_reuses_completed_reservation() -> None:
    collection = ExplicitOverlapCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    messages = [Message("user", ["same"], message_id="explicit-message")]

    winner = asyncio.create_task(
        provider.save_messages("session-1", messages),
        name="winner",
    )
    loser = asyncio.create_task(
        provider.save_messages(
            "session-1",
            [Message("user", ["same"], message_id="explicit-message")],
        ),
        name="loser",
    )
    await winner
    collection.winner_completed.set()
    await loser

    stored_messages = [
        document for document in collection.documents if document.get("_kind") == "message"
    ]
    reservations = [
        document for document in collection.documents if document.get("_kind") == "reservation"
    ]
    assert [document["sequence"] for document in stored_messages] == [1]
    assert len(reservations) == 1
    assert reservations[0]["first_sequence"] == 1
    assert reservations[0]["expires_at"] > reservations[0]["created_at"]


async def test_restored_failed_state_reuses_ids_and_original_sequence_slots() -> None:
    collection = PartialFailureCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    state: dict[str, Any] = {}
    batch = [Message("user", ["first"]), Message("assistant", ["second"])]

    with pytest.raises(MongoDBPersistenceError):
        await provider.save_messages("session-1", batch, state=state)
    restored_state = cast(dict[str, Any], json.loads(json.dumps(state)))
    restored_provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    await restored_provider.save_messages(
        "session-1",
        [Message("user", ["first"]), Message("assistant", ["second"])],
        state=restored_state,
    )

    assert [document["sequence"] for document in message_documents(collection)] == [1, 2]
    assert collection.sequence == 2
    assert "mongodb_history_pending_batches" not in restored_state


async def test_malformed_or_legacy_retry_state_fails_with_migration_guidance() -> None:
    provider = MongoDBHistoryProvider(cast(Any, FakeCollection()), options=options())

    with pytest.raises(MongoDBConfigurationError, match="restore a supported state version"):
        await provider.save_messages(
            "session-1",
            [Message("user", ["hello"])],
            state={"mongodb_history_pending_batches": {"bad": "shape"}},
        )
    with pytest.raises(MongoDBConfigurationError, match="migration"):
        await provider.save_messages(
            "session-1",
            [Message("user", ["hello"])],
            state={"mongodb_history_pending_ids": {"old:0": "legacy-id"}},
        )


def set_index_non_unique(index: dict[str, Any]) -> None:
    index["unique"] = False


def remove_scope_from_index_filter(index: dict[str, Any]) -> None:
    index["partialFilterExpression"] = {"_kind": "message"}


def reorder_index_keys(index: dict[str, Any]) -> None:
    index["key"] = {
        "session_id": 1,
        "scope_discriminator": 1,
        "stable_message_id": 1,
    }


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (set_index_non_unique, "unique"),
        (remove_scope_from_index_filter, "partialFilterExpression"),
        (reorder_index_keys, "keys"),
    ],
)
async def test_validate_indexes_rejects_complete_semantic_mismatches(
    mutation: Any,
    message: str,
) -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(retention=timedelta(days=1)),
    )
    await provider.ensure_indexes()
    collection.regular_indexes = [
        {"key": dict(keys), **definition} for keys, definition in collection.created_indexes
    ]
    mutation(collection.regular_indexes[0])

    with pytest.raises(MongoDBIndexMismatchError, match=message):
        await provider.validate_indexes()


async def test_validate_indexes_rejects_ttl_partial_filter_mismatch() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(
        cast(Any, collection),
        options=options(retention=timedelta(days=1)),
    )
    await provider.ensure_indexes()
    collection.regular_indexes = [
        {"key": dict(keys), **definition} for keys, definition in collection.created_indexes
    ]
    for field, value, expected in (
        ("expireAfterSeconds", 60, "expireAfterSeconds"),
        ("unique", True, "must not be unique"),
        ("partialFilterExpression", {"_kind": "message"}, "partialFilterExpression"),
    ):
        original = collection.regular_indexes[2].get(field)
        collection.regular_indexes[2][field] = value
        with pytest.raises(MongoDBIndexMismatchError, match=expected):
            await provider.validate_indexes()
        if original is None:
            collection.regular_indexes[2].pop(field)
        else:
            collection.regular_indexes[2][field] = original


async def test_validate_indexes_requires_binary_identity_collation() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    await provider.ensure_indexes()
    collection.regular_indexes = [
        {"key": dict(keys), **definition} for keys, definition in collection.created_indexes
    ]

    collection.regular_indexes[0]["collation"] = {"locale": "en", "strength": 2}
    with pytest.raises(MongoDBIndexMismatchError, match="simple.*collation"):
        await provider.validate_indexes()

    collection.regular_indexes[0].pop("collation")
    collection.regular_indexes[1]["collation"] = "simple"
    await provider.validate_indexes()


async def test_validate_indexes_rejects_reservation_ttl_mismatch() -> None:
    collection = FakeCollection()
    provider = MongoDBHistoryProvider(cast(Any, collection), options=options())
    await provider.ensure_indexes()
    collection.regular_indexes = [
        {"key": dict(keys), **definition} for keys, definition in collection.created_indexes
    ]
    collection.regular_indexes[-1]["expireAfterSeconds"] = 60

    with pytest.raises(MongoDBIndexMismatchError, match="history_reservation_ttl"):
        await provider.validate_indexes()
