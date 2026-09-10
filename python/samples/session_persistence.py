"""Persist, resume, version, expire, and delete one authorized AgentSession."""

from __future__ import annotations

import argparse
import asyncio
import os
from datetime import datetime, timedelta, timezone

from agent_framework import AgentSession

from agent_framework_mongodb import (
    MongoDBSessionStore,
    MongoDBSessionStoreOptions,
)


def _required(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise RuntimeError(f"{name} is required.")
    return value


def _positive_seconds(name: str, default: str) -> int:
    raw = os.environ.get(name, default)
    try:
        value = int(raw)
    except ValueError as exc:
        raise RuntimeError(f"{name} must be a positive integer.") from exc
    if value <= 0:
        raise RuntimeError(f"{name} must be a positive integer.")
    return value


async def run(*, keep: bool) -> None:
    """Run the complete session persistence scenario."""
    ttl_seconds = _positive_seconds("MONGODB_SESSION_TTL_SECONDS", "3600")
    store_key = _required("MONGODB_SESSION_ID")
    store = MongoDBSessionStore(
        connection_string=_required("MONGODB_URI"),
        database_name=_required("MONGODB_DATABASE"),
        collection_name=_required("MONGODB_SESSION_COLLECTION"),
        options=MongoDBSessionStoreOptions(
            tenant_id=_required("MONGODB_SESSION_TENANT_ID"),
            application_id=_required("MONGODB_SESSION_APPLICATION_ID"),
            agent_id=_required("MONGODB_SESSION_AGENT_ID"),
            ttl=timedelta(seconds=ttl_seconds),
        ),
    )
    async with store:
        await store.ensure_indexes()
        session = AgentSession(session_id=f"{store_key}-framework")
        session.state["sample"] = {"turn": 1, "status": "created"}
        version = await store.create(
            store_key,
            session,
            expires_at=datetime.now(timezone.utc) + timedelta(seconds=ttl_seconds),
        )
        loaded = await store.get_versioned(store_key)
        if loaded is None:
            raise RuntimeError("The created session was not found.")
        loaded.session.state["sample"] = {"turn": 2, "status": "resumed"}
        version = await store.compare_and_set(
            store_key,
            loaded.session,
            expected_version=loaded.version,
            expires_at=datetime.now(timezone.utc) + timedelta(seconds=ttl_seconds),
        )
        print(f"Created version 1; resumed version {version}; expiration configured.")
        if not keep:
            deleted = await store.compare_and_delete(store_key, expected_version=version)
            print(f"Authorized cleanup deleted {int(deleted)} session.")
        else:
            print("Authorized cleanup skipped by --keep.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--keep",
        action="store_true",
        help="Keep the authorized sample snapshot until its configured expiration.",
    )
    args = parser.parse_args()
    asyncio.run(run(keep=args.keep))


if __name__ == "__main__":
    main()
