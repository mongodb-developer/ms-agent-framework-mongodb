import os
import uuid
from dataclasses import dataclass
from datetime import timedelta
from typing import Any

import pytest
from agent_framework import (
    Executor,
    Workflow,
    WorkflowBuilder,
    WorkflowContext,
    handler,
    response_handler,
)
from pymongo import AsyncMongoClient

from agent_framework_mongodb import (
    MongoDBCheckpointNotFoundError,
    MongoDBCheckpointStorage,
    MongoDBCheckpointStorageOptions,
)

pytestmark = pytest.mark.integration_persistence


@dataclass(frozen=True)
class DeploymentApproval:
    operation: str


@dataclass(frozen=True)
class DeploymentDecision:
    approved: bool


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
            DeploymentApproval(operation),
            DeploymentDecision,
            request_id="deployment-approval",
        )

    @response_handler(
        request=DeploymentApproval,
        response=DeploymentDecision,
        output=str,
        workflow_output=str,
    )
    async def handle_approval(
        self,
        original_request: DeploymentApproval,
        response: DeploymentDecision,
        context: WorkflowContext[str, str],
    ) -> None:
        context.set_state("approved", response.approved)
        await context.yield_output(
            f"{original_request.operation}:{'approved' if response.approved else 'rejected'}"
        )


def _mongodb_uri() -> str:
    uri = os.environ.get("MONGODB_URI", "").strip()
    if not uri:
        pytest.skip("MONGODB_URI is required for integration-persistence tests.")
    return uri


def _workflow(storage: MongoDBCheckpointStorage) -> Workflow:
    return WorkflowBuilder(
        name="deployment-approval",
        start_executor=ApprovalExecutor(),
        checkpoint_storage=storage,
    ).build()


@pytest.mark.asyncio
async def test_checkpoint_storage_resumption_lineage_order_isolation_and_cleanup() -> None:
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(_mongodb_uri())
    database = client[os.environ.get("MONGODB_DATABASE", "agent_framework_mongodb_tests")]
    prefix = f"test-checkpoint-{uuid.uuid4().hex}"
    collection_name = f"{prefix}-workflow"
    collection = database[collection_name]
    allowed_types = (
        f"{DeploymentApproval.__module__}:{DeploymentApproval.__qualname__}",
        f"{DeploymentDecision.__module__}:{DeploymentDecision.__qualname__}",
    )
    first = MongoDBCheckpointStorage(
        collection,
        options=MongoDBCheckpointStorageOptions(
            tenant_id=f"{prefix}-tenant-one",
            application_id=f"{prefix}-app",
            workflow_name="deployment-approval",
            session_id=f"{prefix}-run",
            ttl=timedelta(hours=1),
            page_size=2,
            allowed_checkpoint_types=allowed_types,
        ),
    )
    second = MongoDBCheckpointStorage(
        collection,
        options=MongoDBCheckpointStorageOptions(
            tenant_id=f"{prefix}-tenant-two",
            application_id=f"{prefix}-app",
            workflow_name="deployment-approval",
            session_id=f"{prefix}-run",
            page_size=2,
            allowed_checkpoint_types=allowed_types,
        ),
    )
    try:
        await first.ensure_indexes()
        await first.validate_indexes()
        paused = await _workflow(first).run("deploy")
        request = paused.get_request_info_events()[0]
        paused_checkpoint = await first.get_latest(workflow_name="deployment-approval")
        assert paused_checkpoint is not None
        assert request.request_id in paused_checkpoint.pending_request_info_events
        assert await second.get_latest(workflow_name="deployment-approval") is None

        resumed = await _workflow(first).run(
            checkpoint_id=paused_checkpoint.checkpoint_id,
            responses={
                request.request_id: DeploymentDecision(approved=True),
            },
        )
        assert resumed.get_outputs() == ["deploy:approved"]

        latest = await first.get_latest(workflow_name="deployment-approval")
        assert latest is not None
        assert latest.checkpoint_id != paused_checkpoint.checkpoint_id
        first_page = await first.list_checkpoint_page(
            workflow_name="deployment-approval",
        )
        assert len(first_page.checkpoints) == 2
        assert first_page.next_cursor is not None
        second_page = await first.list_checkpoint_page(
            workflow_name="deployment-approval",
            cursor=first_page.next_cursor,
        )
        assert second_page.checkpoints

        with pytest.raises(MongoDBCheckpointNotFoundError):
            await second.load(latest.checkpoint_id)
        checkpoint_ids = await first.list_checkpoint_ids(workflow_name="deployment-approval")
        assert len(checkpoint_ids) > first.options.page_size
        cleared = await first.clear_run()
        assert cleared.acknowledged
        assert cleared.checkpoints_deleted == len(checkpoint_ids)
        assert cleared.counter_deleted == 1
        assert await first.get_latest(workflow_name="deployment-approval") is None
        assert (
            await collection.count_documents(
                {
                    "tenant_id": first.options.tenant_id,
                    "workflow_name": first.options.workflow_name,
                    "session_id": first.options.session_id,
                }
            )
            == 0
        )
    finally:
        await database.drop_collection(collection_name)
        await client.close()
