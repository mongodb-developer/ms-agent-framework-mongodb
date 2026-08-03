from __future__ import annotations

import json
from pathlib import Path
from typing import Any, cast

from agent_framework import SessionStore

from agent_framework_mongodb import MongoDBSessionStore


def test_session_store_contract_matches_public_surface() -> None:
    fixture_path = Path(__file__).parent / "fixtures" / "session_store_contract.json"
    contract = cast(dict[str, Any], json.loads(fixture_path.read_text(encoding="utf-8")))

    assert issubclass(MongoDBSessionStore, SessionStore)
    assert contract["schema_version"] == MongoDBSessionStore.SCHEMA_VERSION
    assert (
        contract["framework_serialization"] == MongoDBSessionStore.FRAMEWORK_SERIALIZATION_VERSION
    )
    assert contract["collection_default"] == MongoDBSessionStore.DEFAULT_COLLECTION_NAME
    assert contract["scope_dimensions"] == [
        "tenant_id",
        "application_id",
        "agent_id",
        "session_id",
    ]
    assert contract["authorization_dimensions"] == [
        "tenant_id",
        "application_id",
        "agent_id",
    ]
    assert [item["name"] for item in contract["indexes"]] == [
        "session_store_scope_identity",
        "session_store_scope_version",
        "session_store_expiration",
    ]
    assert [
        (item["operation"], item["outcome"], item["new_version"])
        for item in contract["concurrency_cases"]
    ] == [
        ("create", "stored", 1),
        ("create", "idempotent", 1),
        ("create", "conflict", None),
        ("create", "conflict", None),
        ("compare_and_set", "stored", 2),
        ("compare_and_set", "idempotent", 2),
        ("compare_and_set", "conflict", None),
        ("compare_and_set", "conflict", None),
        ("compare_and_delete", "deleted", None),
    ]
