from __future__ import annotations

import asyncio
import os
import uuid
from datetime import datetime, timedelta, timezone
from typing import Any

import pytest
from agent_framework import AgentSession
from pymongo import AsyncMongoClient

from agent_framework_mongodb import (
    MongoDBConcurrencyError,
    MongoDBSessionStore,
    MongoDBSessionStoreOptions,
)

pytestmark = pytest.mark.integration_persistence


def _mongodb_uri() -> str:
    uri = os.environ.get("MONGODB_URI", "").strip()
    if not uri:
        pytest.skip("MONGODB_URI is required for integration-persistence tests.")
    return uri


@pytest.mark.asyncio
async def test_session_store_round_trip_concurrency_isolation_deletion_and_expiration() -> None:
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(_mongodb_uri())
    database = client[os.environ.get("MONGODB_DATABASE", "agent_framework_mongodb_tests")]
    prefix = f"test-session-{uuid.uuid4().hex}"
    collection_name = f"{prefix}-snapshots"
    collection = database[collection_name]
    first_scope = MongoDBSessionStoreOptions(
        tenant_id=f"{prefix}-tenant-one",
        application_id=f"{prefix}-app",
        agent_id=f"{prefix}-agent",
        ttl=timedelta(seconds=2),
    )
    second_scope = MongoDBSessionStoreOptions(
        tenant_id=f"{prefix}-tenant-two",
        application_id=f"{prefix}-app",
        agent_id=f"{prefix}-agent",
        ttl=timedelta(seconds=2),
    )
    first = MongoDBSessionStore(collection, options=first_scope)
    second = MongoDBSessionStore(collection, options=second_scope)
    try:
        await first.ensure_indexes()
        await first.validate_indexes()

        session = AgentSession(session_id=f"{prefix}-framework")
        session.state["unknown-provider"] = {
            "window": ["one", "two"],
            "counter": 7,
        }
        version = await first.create(f"{prefix}-current", session)
        loaded = await first.get_versioned(f"{prefix}-current")
        assert loaded is not None
        assert loaded.version == version == 1
        assert loaded.session.to_dict() == session.to_dict()
        assert await second.get(f"{prefix}-current") is None

        loaded.session.state["unknown-provider"]["counter"] = 8
        version = await first.compare_and_set(
            f"{prefix}-current",
            loaded.session,
            expected_version=version,
        )
        assert version == 2
        with pytest.raises(MongoDBConcurrencyError):
            await first.compare_and_set(
                f"{prefix}-current",
                session,
                expected_version=1,
            )
        assert await first.compare_and_delete(
            f"{prefix}-current",
            expected_version=version,
        )
        assert await first.get(f"{prefix}-current") is None

        expiring = AgentSession(session_id=f"{prefix}-expiring-framework")
        await first.create(
            f"{prefix}-expiring",
            expiring,
            expires_at=datetime.now(timezone.utc) + timedelta(seconds=2),
        )
        deadline = asyncio.get_running_loop().time() + 120
        while await first.get(f"{prefix}-expiring") is not None:
            if asyncio.get_running_loop().time() >= deadline:
                pytest.fail("MongoDB TTL did not remove the session within 120 seconds.")
            await asyncio.sleep(2)
    finally:
        await database.drop_collection(collection_name)
        await client.close()
