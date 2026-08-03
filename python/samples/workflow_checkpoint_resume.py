"""Pause an Agent Framework workflow for approval and resume it from MongoDB."""

import argparse
import asyncio
import os
from dataclasses import dataclass
from datetime import timedelta

from agent_framework import (
    Executor,
    Workflow,
    WorkflowBuilder,
    WorkflowContext,
    handler,
    response_handler,
)

from agent_framework_mongodb import (
    MongoDBCheckpointStorage,
    MongoDBCheckpointStorageOptions,
)


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


def _required(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise RuntimeError(f"{name} is required.")
    return value


def _positive_seconds(name: str, default: str) -> int:
    try:
        value = int(os.environ.get(name, default))
    except ValueError as exc:
        raise RuntimeError(f"{name} must be a positive integer.") from exc
    if value <= 0:
        raise RuntimeError(f"{name} must be a positive integer.")
    return value


def _build_workflow(storage: MongoDBCheckpointStorage) -> Workflow:
    return WorkflowBuilder(
        name=storage.options.workflow_name,
        start_executor=ApprovalExecutor(),
        checkpoint_storage=storage,
    ).build()


async def _checkpoint_ids(storage: MongoDBCheckpointStorage) -> list[str]:
    checkpoint_ids: list[str] = []
    cursor: str | None = None
    while True:
        page = await storage.list_checkpoint_page(
            workflow_name=storage.options.workflow_name,
            cursor=cursor,
        )
        checkpoint_ids.extend(item.checkpoint_id for item in page.checkpoints)
        if page.next_cursor is None:
            return checkpoint_ids
        cursor = page.next_cursor


async def run(*, keep: bool) -> None:
    """Run the complete pending-approval checkpoint resumption scenario."""
    ttl = timedelta(seconds=_positive_seconds("MONGODB_CHECKPOINT_TTL_SECONDS", "3600"))
    storage = MongoDBCheckpointStorage(
        connection_string=_required("MONGODB_URI"),
        database_name=_required("MONGODB_DATABASE"),
        collection_name=_required("MONGODB_CHECKPOINT_COLLECTION"),
        options=MongoDBCheckpointStorageOptions(
            tenant_id=_required("MONGODB_CHECKPOINT_TENANT_ID"),
            application_id=os.environ.get("MONGODB_CHECKPOINT_APPLICATION_ID"),
            workflow_name=_required("MONGODB_CHECKPOINT_WORKFLOW_NAME"),
            session_id=_required("MONGODB_CHECKPOINT_SESSION_ID"),
            ttl=ttl,
            page_size=10,
            allowed_checkpoint_types=(
                f"{DeploymentApproval.__module__}:{DeploymentApproval.__qualname__}",
                f"{DeploymentDecision.__module__}:{DeploymentDecision.__qualname__}",
            ),
        ),
    )
    async with storage:
        await storage.ensure_indexes()
        paused = await _build_workflow(storage).run("deploy")
        request = paused.get_request_info_events()[0]
        checkpoint = await storage.get_latest(workflow_name=storage.options.workflow_name)
        if checkpoint is None:
            raise RuntimeError("The workflow did not persist its pending approval.")
        print("Paused with one pending approval checkpoint.")

        resumed = await _build_workflow(storage).run(
            checkpoint_id=checkpoint.checkpoint_id,
            responses={request.request_id: DeploymentDecision(approved=True)},
        )
        print(f"Resumed output: {resumed.get_outputs()[0]}")
        latest = await storage.get_latest(workflow_name=storage.options.workflow_name)
        if latest is None:
            raise RuntimeError("The resumed workflow did not persist a checkpoint.")
        first_page = await storage.list_checkpoint_page(workflow_name=storage.options.workflow_name)
        print(
            f"Latest checkpoint found; first page contains "
            f"{len(first_page.checkpoints)} checkpoint(s)."
        )

        if keep:
            print("Authorized cleanup skipped by --keep; TTL expiration remains eventual.")
        else:
            checkpoint_ids = await _checkpoint_ids(storage)
            deleted = 0
            for checkpoint_id in checkpoint_ids:
                deleted += int(await storage.delete(checkpoint_id))
            print(f"Authorized cleanup deleted {deleted} checkpoint(s).")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--keep",
        action="store_true",
        help="Keep checkpoints until MongoDB's asynchronous TTL monitor removes them.",
    )
    args = parser.parse_args()
    asyncio.run(run(keep=args.keep))


if __name__ == "__main__":
    main()
