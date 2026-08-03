import asyncio
import copy
import json
import os
import subprocess
import sys
from collections import OrderedDict
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, cast
from unittest.mock import patch

import pytest
from agent_framework import (
    CheckpointStorage,
    Executor,
    WorkflowBuilder,
    WorkflowCheckpoint,
    WorkflowCheckpointException,
    WorkflowContext,
    WorkflowEvent,
    WorkflowMessage,
    handler,
    response_handler,
)
from pymongo import ASCENDING, DESCENDING, ReturnDocument
from pymongo.errors import ConnectionFailure, DuplicateKeyError

from agent_framework_mongodb import (
    MongoDBCheckpointNotFoundError,
    MongoDBCheckpointPage,
    MongoDBCheckpointStorage,
    MongoDBCheckpointStorageOptions,
    MongoDBConcurrencyError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBMappingError,
    MongoDBTransientPersistenceError,
    MongoDBTransientRetrievalError,
)
from agent_framework_mongodb._shared.client import MongoClientHandle


class Result:
    def __init__(self, *, deleted_count: int = 0) -> None:
        self.deleted_count = deleted_count


_REMOVE = object()


def evaluate_update_expression(expression: object, document: dict[str, Any]) -> object:
    if isinstance(expression, str):
        if expression == "$$REMOVE":
            return _REMOVE
        if expression.startswith("$"):
            return document.get(expression[1:])
        return expression
    if isinstance(expression, list):
        values = cast(list[object], expression)
        return [evaluate_update_expression(item, document) for item in values]
    if not isinstance(expression, dict):
        return expression
    operators = cast(dict[str, object], expression)
    if "$add" in operators:
        values = evaluate_update_expression(operators["$add"], document)
        return sum(cast(list[int], values))
    if "$ifNull" in operators:
        values = cast(
            list[object],
            evaluate_update_expression(operators["$ifNull"], document),
        )
        return values[1] if values[0] is None else values[0]
    if "$cond" in operators:
        condition, when_true, when_false = cast(list[object], operators["$cond"])
        branch = when_true if evaluate_update_expression(condition, document) else when_false
        return evaluate_update_expression(branch, document)
    if "$eq" in operators:
        values = cast(
            list[object],
            evaluate_update_expression(operators["$eq"], document),
        )
        return values[0] == values[1]
    if "$ne" in operators:
        values = cast(
            list[object],
            evaluate_update_expression(operators["$ne"], document),
        )
        return values[0] != values[1]
    if "$gt" in operators:
        values = cast(
            list[Any],
            evaluate_update_expression(operators["$gt"], document),
        )
        return bool(values[0] > values[1])
    if "$and" in operators:
        values = cast(
            list[object],
            evaluate_update_expression(operators["$and"], document),
        )
        return all(bool(value) for value in values)
    if "$or" in operators:
        values = cast(
            list[object],
            evaluate_update_expression(operators["$or"], document),
        )
        return any(bool(value) for value in values)
    raise AssertionError(f"Unsupported fake update expression: {operators}")


class FakeCursor:
    def __init__(self, documents: list[dict[str, Any]], *, cancel: bool = False) -> None:
        self.documents = documents
        self.cancel = cancel

    def sort(self, keys: list[tuple[str, int]]) -> "FakeCursor":
        for key, direction in reversed(keys):
            self.documents.sort(
                key=lambda document: cast(str | int, document[key]),
                reverse=direction == DESCENDING,
            )
        return self

    def limit(self, count: int) -> "FakeCursor":
        self.documents = self.documents[:count]
        return self

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        if self.cancel:
            raise asyncio.CancelledError
        return copy.deepcopy(self.documents if length is None else self.documents[:length])


class FakeIndexCursor:
    def __init__(self, indexes: list[dict[str, Any]]) -> None:
        self.indexes = indexes

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        del length
        return copy.deepcopy(self.indexes)


class FakeCollection:
    def __init__(self) -> None:
        self.documents: list[dict[str, Any]] = []
        self.deleted_filters: list[dict[str, Any]] = []
        self.created_indexes: list[tuple[Any, dict[str, Any]]] = []
        self.regular_indexes: list[dict[str, Any]] = []
        self.fail_reads = False
        self.fail_writes = False
        self.cancel_writes = False
        self.cancel_list_call: int | None = None
        self.find_calls = 0

    async def find_one(
        self,
        query: dict[str, Any],
        *,
        sort: list[tuple[str, int]] | None = None,
    ) -> dict[str, Any] | None:
        if self.fail_reads:
            raise ConnectionFailure("private-host.invalid")
        matches = [document for document in self.documents if matches_query(document, query)]
        if sort:
            matches = FakeCursor(matches).sort(sort).documents
        return copy.deepcopy(matches[0]) if matches else None

    def find(self, query: dict[str, Any]) -> FakeCursor:
        if self.fail_reads:
            raise ConnectionFailure("private-host.invalid")
        self.find_calls += 1
        return FakeCursor(
            [copy.deepcopy(item) for item in self.documents if matches_query(item, query)],
            cancel=self.find_calls == self.cancel_list_call,
        )

    async def find_one_and_update(
        self,
        query: dict[str, Any],
        update: dict[str, Any] | list[dict[str, Any]],
        *,
        upsert: bool,
        return_document: ReturnDocument,
    ) -> dict[str, Any]:
        del return_document
        if self.cancel_writes:
            raise asyncio.CancelledError
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        document = next(
            (document for document in self.documents if matches_query(document, query)),
            None,
        )
        if document is None:
            if not upsert:
                raise AssertionError("counter update must upsert")
            document = copy.deepcopy(query)
            self.documents.append(document)
        if isinstance(update, list):
            for stage in update:
                source = copy.deepcopy(document)
                changes = {
                    key: evaluate_update_expression(expression, source)
                    for key, expression in cast(dict[str, Any], stage["$set"]).items()
                }
                for key, value in changes.items():
                    if value is _REMOVE:
                        document.pop(key, None)
                    else:
                        document[key] = value
        else:
            document.update(copy.deepcopy(update.get("$setOnInsert", {})))
            document["sequence"] = document.get("sequence", 0) + cast(
                int,
                update["$inc"]["sequence"],
            )
            document.update(copy.deepcopy(update.get("$set", {})))
        return copy.deepcopy(document)

    async def insert_one(self, document: dict[str, Any]) -> Result:
        if self.cancel_writes:
            raise asyncio.CancelledError
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        if any(item["_id"] == document["_id"] for item in self.documents):
            raise DuplicateKeyError("duplicate")
        self.documents.append(copy.deepcopy(document))
        return Result()

    async def delete_one(self, query: dict[str, Any]) -> Result:
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        self.deleted_filters.append(copy.deepcopy(query))
        for index, document in enumerate(self.documents):
            if matches_query(document, query):
                del self.documents[index]
                return Result(deleted_count=1)
        return Result()

    async def delete_many(self, query: dict[str, Any]) -> Result:
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        self.deleted_filters.append(copy.deepcopy(query))
        retained = [item for item in self.documents if not matches_query(item, query)]
        deleted_count = len(self.documents) - len(retained)
        self.documents = retained
        return Result(deleted_count=deleted_count)

    async def create_index(self, keys: Any, **kwargs: Any) -> str:
        self.created_indexes.append((keys, copy.deepcopy(kwargs)))
        return cast(str, kwargs["name"])

    async def list_indexes(self) -> FakeIndexCursor:
        return FakeIndexCursor(self.regular_indexes)


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


@dataclass(frozen=True)
class ApprovalRequest:
    operation: str


@dataclass(frozen=True)
class ApprovalResponse:
    approved: bool


class UnsupportedMapping(dict[str, int]):
    pass


class ApprovalExecutor(Executor):
    def __init__(self) -> None:
        super().__init__(id="approver")

    @handler(input=str, output=str, workflow_output=str)
    async def request_approval(
        self,
        operation: str,
        context: WorkflowContext[str, str],
    ) -> None:
        context.set_state("operation", operation)
        await context.request_info(
            ApprovalRequest(operation),
            ApprovalResponse,
            request_id="approval-request",
        )

    @response_handler(
        request=ApprovalRequest,
        response=ApprovalResponse,
        output=str,
        workflow_output=str,
    )
    async def handle_approval(
        self,
        original_request: ApprovalRequest,
        response: ApprovalResponse,
        context: WorkflowContext[str, str],
    ) -> None:
        context.set_state("approved", response.approved)
        await context.yield_output(
            f"{original_request.operation}:{'approved' if response.approved else 'rejected'}"
        )


def matches_query(document: dict[str, Any], query: dict[str, Any]) -> bool:
    for key, value in query.items():
        if key == "$or":
            clauses = cast(list[dict[str, Any]], value)
            if not any(matches_query(document, clause) for clause in clauses):
                return False
        elif isinstance(value, dict) and "$gt" in value:
            if document.get(key) is None or document[key] <= value["$gt"]:
                return False
        elif document.get(key) != value:
            return False
    return True


def options(**overrides: Any) -> MongoDBCheckpointStorageOptions:
    values: dict[str, Any] = {
        "tenant_id": "tenant-1",
        "workflow_name": "approval-workflow",
        "session_id": "run-1",
        "page_size": 2,
    }
    values.update(overrides)
    return MongoDBCheckpointStorageOptions(**values)


def checkpoint(
    checkpoint_id: str,
    *,
    previous_checkpoint_id: str | None = None,
    iteration_count: int = 0,
) -> WorkflowCheckpoint:
    approval = WorkflowEvent(
        "request_info",
        {"prompt": "Approve deployment?", "approved": None},
        executor_id="approver",
        request_id=f"request-{checkpoint_id}",
    )
    return WorkflowCheckpoint(
        workflow_name="approval-workflow",
        graph_signature_hash="graph-v1",
        checkpoint_id=checkpoint_id,
        previous_checkpoint_id=previous_checkpoint_id,
        messages={
            "approver": [
                WorkflowMessage(
                    data={"request": checkpoint_id},
                    source_id="approver",
                    target_id="deployer",
                )
            ]
        },
        state={
            "phase": "waiting",
            "_executor_state": {"approver": {"attempt": iteration_count + 1}},
        },
        pending_request_info_events={approval.request_id or "request": approval},
        iteration_count=iteration_count,
        metadata={"branch": "main"},
    )


def checkpoint_documents(collection: FakeCollection) -> list[dict[str, Any]]:
    return [item for item in collection.documents if item.get("_kind") == "workflow_checkpoint"]


def test_checkpoint_storage_uses_exact_public_framework_contract() -> None:
    storage = MongoDBCheckpointStorage(cast(Any, FakeCollection()), options=options())

    assert CheckpointStorage in type(storage).__mro__
    assert MongoDBCheckpointStorage.save.__annotations__["checkpoint"] == "WorkflowCheckpoint"
    assert MongoDBCheckpointStorage.load.__annotations__["checkpoint_id"] == "CheckpointID"


@pytest.mark.asyncio
async def test_round_trip_preserves_pending_approval_executor_state_and_lineage() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(cast(Any, collection), options=options())
    original = checkpoint("checkpoint-2", previous_checkpoint_id="checkpoint-1", iteration_count=7)

    assert await storage.save(original) == "checkpoint-2"
    restored = await storage.load("checkpoint-2")

    assert restored.checkpoint_id == original.checkpoint_id
    assert restored.previous_checkpoint_id == original.previous_checkpoint_id
    assert restored.messages == original.messages
    assert restored.state == original.state
    assert restored.iteration_count == original.iteration_count
    restored_approval = restored.pending_request_info_events["request-checkpoint-2"]
    original_approval = original.pending_request_info_events["request-checkpoint-2"]
    assert restored_approval.type == original_approval.type
    assert restored_approval.data == original_approval.data
    assert restored_approval.request_id == original_approval.request_id
    assert restored is not original
    document = checkpoint_documents(collection)[0]
    assert document["checkpoint_id"] == "checkpoint-2"
    assert document["parent_checkpoint_id"] == "checkpoint-1"
    assert document["sequence"] == 1
    assert document["payload_version"] == "1.0"


@pytest.mark.asyncio
async def test_public_workflow_resumes_pending_approval_from_mongodb_checkpoint() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(
            allowed_checkpoint_types=(
                f"{ApprovalRequest.__module__}:{ApprovalRequest.__qualname__}",
                f"{ApprovalResponse.__module__}:{ApprovalResponse.__qualname__}",
            )
        ),
    )
    first_workflow = WorkflowBuilder(
        name="approval-workflow",
        start_executor=ApprovalExecutor(),
        checkpoint_storage=storage,
    ).build()

    paused = await first_workflow.run("deploy")
    request = paused.get_request_info_events()[0]
    latest = await storage.get_latest(workflow_name="approval-workflow")
    assert latest is not None
    assert request.request_id in latest.pending_request_info_events

    resumed_workflow = WorkflowBuilder(
        name="approval-workflow",
        start_executor=ApprovalExecutor(),
        checkpoint_storage=storage,
    ).build()
    resumed = await resumed_workflow.run(
        checkpoint_id=latest.checkpoint_id,
        responses={request.request_id: ApprovalResponse(approved=True)},
    )

    assert resumed.get_outputs() == ["deploy:approved"]
    resumed_latest = await storage.get_latest(workflow_name="approval-workflow")
    assert resumed_latest is not None
    ancestor_ids: set[str] = set()
    current = resumed_latest
    while current.previous_checkpoint_id is not None:
        ancestor_ids.add(current.previous_checkpoint_id)
        current = await storage.load(current.previous_checkpoint_id)
    assert latest.checkpoint_id in ancestor_ids


@pytest.mark.asyncio
async def test_save_is_idempotent_and_rejects_same_id_with_conflicting_payload() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(cast(Any, collection), options=options())
    original = checkpoint("stable-id")

    assert await storage.save(original) == "stable-id"
    assert await storage.save(copy.deepcopy(original)) == "stable-id"
    assert len(checkpoint_documents(collection)) == 1

    conflicting = checkpoint("stable-id")
    conflicting.state["phase"] = "changed"
    with pytest.raises(MongoDBConcurrencyError, match="different payload"):
        await storage.save(conflicting)


@pytest.mark.asyncio
async def test_plain_dict_order_is_logically_insensitive_but_ordered_dict_conflicts() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(cast(Any, collection), options=options())
    plain = checkpoint("plain")
    plain.state["mapping"] = {"alpha": 1, "beta": 2}
    reordered_plain = copy.deepcopy(plain)
    reordered_plain.state["mapping"] = {"beta": 2, "alpha": 1}

    assert plain.to_dict()["state"] == reordered_plain.to_dict()["state"]
    assert await storage.save(plain) == "plain"
    assert await storage.save(reordered_plain) == "plain"
    ordered_instead_of_plain = copy.deepcopy(plain)
    ordered_instead_of_plain.state["mapping"] = OrderedDict([("alpha", 1), ("beta", 2)])
    with pytest.raises(MongoDBConcurrencyError, match="different payload"):
        await storage.save(ordered_instead_of_plain)

    ordered = checkpoint("ordered")
    ordered.state["mapping"] = OrderedDict([("alpha", 1), ("beta", 2)])
    assert await storage.save(ordered) == "ordered"
    assert await storage.save(copy.deepcopy(ordered)) == "ordered"
    reversed_order = copy.deepcopy(ordered)
    reversed_order.state["mapping"] = OrderedDict([("beta", 2), ("alpha", 1)])

    assert ordered.state["mapping"] != reversed_order.state["mapping"]
    with pytest.raises(MongoDBConcurrencyError, match="different payload"):
        await storage.save(reversed_order)


@pytest.mark.asyncio
async def test_unsupported_mapping_subclass_has_stable_migration_guidance() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(cast(Any, collection), options=options())
    invalid = checkpoint("unsupported-mapping")
    invalid.state["mapping"] = UnsupportedMapping(alpha=1)

    with pytest.raises(MongoDBMappingError, match="noncanonical.*migrate"):
        await storage.save(invalid)
    assert collection.documents == []


@pytest.mark.asyncio
async def test_concurrent_saves_have_unique_monotonic_sequence_order() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(page_size=10),
    )

    await asyncio.gather(*(storage.save(checkpoint(f"checkpoint-{index}")) for index in range(8)))

    documents = checkpoint_documents(collection)
    assert sorted(item["sequence"] for item in documents) == list(range(1, 9))
    listed = await storage.list_checkpoints(workflow_name="approval-workflow")
    assert [item.checkpoint_id for item in listed] == [
        item["checkpoint_id"] for item in sorted(documents, key=lambda item: item["sequence"])
    ]
    latest = await storage.get_latest(workflow_name="approval-workflow")
    assert latest is not None
    assert latest.checkpoint_id == listed[-1].checkpoint_id


@pytest.mark.asyncio
async def test_bounded_cursor_pagination_and_id_listing_are_deterministic() -> None:
    storage = MongoDBCheckpointStorage(cast(Any, FakeCollection()), options=options())
    for index in range(5):
        await storage.save(
            checkpoint(
                f"checkpoint-{index}",
                previous_checkpoint_id=f"checkpoint-{index - 1}" if index else None,
            )
        )

    first = await storage.list_checkpoint_page(workflow_name="approval-workflow")
    assert isinstance(first, MongoDBCheckpointPage)
    assert [item.checkpoint_id for item in first.checkpoints] == [
        "checkpoint-0",
        "checkpoint-1",
    ]
    assert first.next_cursor is not None
    second = await storage.list_checkpoint_page(
        workflow_name="approval-workflow",
        cursor=first.next_cursor,
    )
    third = await storage.list_checkpoint_page(
        workflow_name="approval-workflow",
        cursor=second.next_cursor,
    )
    assert [item.checkpoint_id for item in second.checkpoints] == [
        "checkpoint-2",
        "checkpoint-3",
    ]
    assert [item.checkpoint_id for item in third.checkpoints] == ["checkpoint-4"]
    assert third.next_cursor is None
    assert [
        item.checkpoint_id
        for item in await storage.list_checkpoints(workflow_name="approval-workflow")
    ] == [f"checkpoint-{index}" for index in range(5)]
    assert await storage.list_checkpoint_ids(workflow_name="approval-workflow") == [
        f"checkpoint-{index}" for index in range(5)
    ]


@pytest.mark.asyncio
async def test_inherited_listing_propagates_cancellation_between_bounded_pages() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(cast(Any, collection), options=options())
    for index in range(5):
        await storage.save(checkpoint(f"checkpoint-{index}"))
    collection.cancel_list_call = 2

    with pytest.raises(asyncio.CancelledError):
        await storage.list_checkpoints(workflow_name="approval-workflow")
    assert collection.find_calls == 2


def test_idempotency_hash_is_stable_across_python_hash_seeds() -> None:
    fixture_path = (
        Path(__file__).parents[1] / "contracts" / "fixtures" / "checkpoint_canonical_hash.json"
    )
    expected = cast(dict[str, str], json.loads(fixture_path.read_text(encoding="utf-8")))
    script = """
from agent_framework import WorkflowCheckpoint
from agent_framework_mongodb.checkpointing.store import _logical_payload_hash
checkpoint = WorkflowCheckpoint(
    workflow_name="approval-workflow",
    graph_signature_hash="graph-v1",
    checkpoint_id="checkpoint-stable",
    previous_checkpoint_id="checkpoint-parent",
    timestamp="2030-01-02T03:04:05+00:00",
    state={"labels": {"beta", "alpha"}, "nested": {"b": 2, "a": 1}},
    iteration_count=7,
    metadata={"attempt": 1},
)
print(_logical_payload_hash(checkpoint, frozenset()))
"""
    observed: list[str] = []
    for seed in ("1", "987654"):
        environment = {**os.environ, "PYTHONHASHSEED": seed}
        result = subprocess.run(
            [sys.executable, "-c", script],
            check=True,
            capture_output=True,
            text=True,
            env=environment,
        )
        observed.append(result.stdout.strip())

    assert observed == [expected["sha256"], expected["sha256"]]


@pytest.mark.asyncio
async def test_save_rejects_noncanonical_state_before_allocating_sequence() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(cast(Any, collection), options=options())
    invalid = checkpoint("unsupported")
    invalid.state["unsupported"] = object()

    with pytest.raises(MongoDBMappingError, match="canonical"):
        await storage.save(invalid)
    assert collection.documents == []


@pytest.mark.asyncio
async def test_scope_is_mandatory_and_all_operations_are_authorized_before_id_lookup() -> None:
    collection = FakeCollection()
    tenant_one = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(tenant_id="tenant-1"),
    )
    tenant_two = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(tenant_id="tenant-2"),
    )
    await tenant_one.save(checkpoint("same-id"))

    with pytest.raises(MongoDBCheckpointNotFoundError, match="No checkpoint") as not_found:
        await tenant_two.load("same-id")
    assert isinstance(not_found.value, WorkflowCheckpointException)
    assert await tenant_two.get_latest(workflow_name="approval-workflow") is None
    assert not await tenant_two.delete("same-id")
    assert await tenant_one.delete("same-id")

    delete_filter = collection.deleted_filters[-1]
    assert delete_filter["_id"]
    assert delete_filter["_kind"] == "workflow_checkpoint"
    assert delete_filter["tenant_id"] == "tenant-1"
    assert delete_filter["workflow_name"] == "approval-workflow"
    assert delete_filter["session_id"] == "run-1"


@pytest.mark.asyncio
async def test_workflow_name_cannot_escape_constructor_bound_scope() -> None:
    storage = MongoDBCheckpointStorage(cast(Any, FakeCollection()), options=options())
    wrong_workflow = checkpoint("wrong")
    wrong_workflow.workflow_name = "other-workflow"

    with pytest.raises(ValueError, match="bound workflow_name"):
        await storage.save(wrong_workflow)
    with pytest.raises(ValueError, match="bound workflow_name"):
        await storage.list_checkpoints(workflow_name="other-workflow")


@pytest.mark.asyncio
async def test_expiration_can_leave_documented_lineage_gaps() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(ttl=timedelta(hours=1)),
    )
    await storage.save(checkpoint("parent"))
    await storage.save(checkpoint("child", previous_checkpoint_id="parent"))
    counter = next(
        item for item in collection.documents if item["_kind"] == "workflow_checkpoint_counter"
    )
    parent_document = next(
        item for item in checkpoint_documents(collection) if item["checkpoint_id"] == "parent"
    )
    assert cast(datetime, parent_document["expires_at"]).tzinfo is timezone.utc
    assert counter["expires_at"] == checkpoint_documents(collection)[-1]["expires_at"]

    collection.documents.remove(parent_document)
    child = await storage.load("child")
    assert child.previous_checkpoint_id == "parent"


@pytest.mark.asyncio
async def test_counter_expiration_uses_max_then_permanent_retention_without_reset() -> None:
    collection = FakeCollection()
    longer = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(ttl=timedelta(hours=2)),
    )
    shorter = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(ttl=timedelta(hours=1)),
    )
    permanent = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(ttl=None),
    )

    await longer.save(checkpoint("longer"))
    long_expiry = next(
        item["expires_at"]
        for item in checkpoint_documents(collection)
        if item["checkpoint_id"] == "longer"
    )
    await shorter.save(checkpoint("shorter"))
    counter = next(
        item for item in collection.documents if item["_kind"] == "workflow_checkpoint_counter"
    )
    assert counter["expires_at"] == long_expiry
    assert counter["retention_mode"] == "ttl"

    await permanent.save(checkpoint("permanent"))
    assert "expires_at" not in counter
    assert counter["retention_mode"] == "permanent"

    await shorter.save(checkpoint("ttl-after-permanent"))
    assert "expires_at" not in counter
    assert counter["retention_mode"] == "permanent"
    assert counter["sequence"] == 4
    assert sorted(item["sequence"] for item in checkpoint_documents(collection)) == [1, 2, 3, 4]


@pytest.mark.asyncio
async def test_clear_run_deletes_only_authorized_checkpoints_and_counter_with_counts() -> None:
    collection = FakeCollection()
    first = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(tenant_id="tenant-1"),
    )
    second = MongoDBCheckpointStorage(
        cast(Any, collection),
        options=options(tenant_id="tenant-2"),
    )
    for index in range(3):
        await first.save(checkpoint(f"first-{index}"))
    await second.save(checkpoint("second-0"))

    result = await first.clear_run()

    assert result.acknowledged
    assert result.checkpoints_deleted == 3
    assert result.counter_deleted == 1
    assert {(item["_kind"], item["tenant_id"]) for item in collection.documents} == {
        ("workflow_checkpoint", "tenant-2"),
        ("workflow_checkpoint_counter", "tenant-2"),
    }
    for query in collection.deleted_filters[-2:]:
        assert query["scope_discriminator"]
        assert query["tenant_id"] == "tenant-1"
        assert query["workflow_name"] == "approval-workflow"
        assert query["session_id"] == "run-1"


@pytest.mark.asyncio
async def test_schema_framework_and_payload_versions_are_migration_gated() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(cast(Any, collection), options=options())
    await storage.save(checkpoint("checkpoint-1"))
    document = checkpoint_documents(collection)[0]

    document["schema_version"] = 999
    with pytest.raises(MongoDBMappingError, match="migrate"):
        await storage.load("checkpoint-1")
    document["schema_version"] = 1
    document["framework_version"] = "future"
    with pytest.raises(MongoDBMappingError, match="supported Agent Framework"):
        await storage.load("checkpoint-1")
    document["framework_version"] = MongoDBCheckpointStorage.FRAMEWORK_SERIALIZATION_VERSION
    document["payload_version"] = "2.0"
    with pytest.raises(MongoDBMappingError, match="payload version"):
        await storage.load("checkpoint-1")

    unsupported = checkpoint("future")
    unsupported.version = "2.0"
    with pytest.raises(MongoDBMappingError, match="payload version"):
        await storage.save(unsupported)


@pytest.mark.asyncio
async def test_index_operations_are_explicit_and_validate_required_definitions() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(cast(Any, collection), options=options())

    assert collection.created_indexes == []
    assert await storage.ensure_indexes() == (
        "checkpoint_scope_identity",
        "checkpoint_scope_sequence",
        "checkpoint_scope_lineage",
        "checkpoint_expiration",
        "checkpoint_counter_expiration",
    )
    expected_partial = {
        "_kind": "workflow_checkpoint",
        "scope_discriminator": {"$type": "string"},
    }
    expected_counter_partial = {
        "_kind": "workflow_checkpoint_counter",
        "scope_discriminator": {"$type": "string"},
    }
    assert collection.created_indexes == [
        (
            [
                ("scope_discriminator", ASCENDING),
                ("workflow_name", ASCENDING),
                ("session_id", ASCENDING),
                ("checkpoint_id", ASCENDING),
            ],
            {
                "name": "checkpoint_scope_identity",
                "unique": True,
                "collation": {"locale": "simple"},
                "partialFilterExpression": expected_partial,
            },
        ),
        (
            [
                ("scope_discriminator", ASCENDING),
                ("workflow_name", ASCENDING),
                ("session_id", ASCENDING),
                ("sequence", ASCENDING),
            ],
            {
                "name": "checkpoint_scope_sequence",
                "unique": True,
                "collation": {"locale": "simple"},
                "partialFilterExpression": expected_partial,
            },
        ),
        (
            [
                ("scope_discriminator", ASCENDING),
                ("workflow_name", ASCENDING),
                ("session_id", ASCENDING),
                ("parent_checkpoint_id", ASCENDING),
            ],
            {
                "name": "checkpoint_scope_lineage",
                "collation": {"locale": "simple"},
                "partialFilterExpression": expected_partial,
            },
        ),
        (
            [("expires_at", ASCENDING)],
            {
                "name": "checkpoint_expiration",
                "expireAfterSeconds": 0,
                "partialFilterExpression": expected_partial,
            },
        ),
        (
            [("expires_at", ASCENDING)],
            {
                "name": "checkpoint_counter_expiration",
                "expireAfterSeconds": 0,
                "partialFilterExpression": expected_counter_partial,
            },
        ),
    ]

    with pytest.raises(MongoDBIndexMissingError, match="scope_identity"):
        await storage.validate_indexes()
    collection.regular_indexes = [
        {
            "name": kwargs["name"],
            "key": dict(keys),
            **{key: value for key, value in kwargs.items() if key != "name"},
        }
        for keys, kwargs in collection.created_indexes
    ]
    await storage.validate_indexes()
    collection.regular_indexes[1]["unique"] = False
    with pytest.raises(MongoDBIndexMismatchError, match="scope_sequence"):
        await storage.validate_indexes()


@pytest.mark.asyncio
async def test_driver_errors_are_typed_and_cancellation_propagates() -> None:
    collection = FakeCollection()
    storage = MongoDBCheckpointStorage(cast(Any, collection), options=options())
    collection.fail_reads = True
    with pytest.raises(MongoDBTransientRetrievalError) as read_error:
        await storage.load("checkpoint-1")
    assert isinstance(read_error.value.__cause__, ConnectionFailure)

    collection.fail_reads = False
    collection.fail_writes = True
    with pytest.raises(MongoDBTransientPersistenceError) as write_error:
        await storage.save(checkpoint("checkpoint-1"))
    assert isinstance(write_error.value.__cause__, ConnectionFailure)

    collection.fail_writes = False
    collection.cancel_writes = True
    with pytest.raises(asyncio.CancelledError):
        await storage.save(checkpoint("checkpoint-2"))


def test_options_require_complete_scope_and_bounded_pagination() -> None:
    with pytest.raises(ValueError, match="tenant_id"):
        MongoDBCheckpointStorageOptions(
            workflow_name="approval-workflow",
            session_id="run-1",
        )
    with pytest.raises(ValueError, match="workflow_name"):
        MongoDBCheckpointStorageOptions(tenant_id="tenant-1", session_id="run-1")
    with pytest.raises(ValueError, match="session_id"):
        MongoDBCheckpointStorageOptions(
            tenant_id="tenant-1",
            workflow_name="approval-workflow",
        )
    with pytest.raises(ValueError, match="page_size"):
        options(page_size=0)
    with pytest.raises(ValueError, match="max_page_size"):
        options(page_size=3, max_page_size=2)


@pytest.mark.asyncio
async def test_client_ownership_is_immutable_and_cleanup_is_idempotent() -> None:
    injected = FakeClient()
    injected_storage = MongoDBCheckpointStorage(
        options=options(),
        mongo_client=cast(Any, injected),
    )
    assert not injected_storage.owns_client
    await injected_storage.close()
    assert injected.close_count == 0

    created = FakeClient()
    with patch(
        "agent_framework_mongodb.checkpointing.store.MongoClientHandle.from_uri",
        return_value=MongoClientHandle(created, owns_client=True),
    ):
        owned_storage = MongoDBCheckpointStorage(options=options())
    assert owned_storage.owns_client
    await owned_storage.close()
    await owned_storage.close()
    assert created.close_count == 1
