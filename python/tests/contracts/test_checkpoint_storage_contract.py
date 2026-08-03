from __future__ import annotations

import json
from pathlib import Path
from typing import Any, cast

from agent_framework import CheckpointStorage

from agent_framework_mongodb import (
    MongoDBCheckpointStorage,
    MongoDBCheckpointStorageOptions,
)


def test_checkpoint_storage_contract_matches_public_surface() -> None:
    fixture_path = Path(__file__).parent / "fixtures" / "checkpoint_storage_contract.json"
    contract = cast(dict[str, Any], json.loads(fixture_path.read_text(encoding="utf-8")))

    assert CheckpointStorage in MongoDBCheckpointStorage.__mro__
    assert contract["schema_version"] == MongoDBCheckpointStorage.SCHEMA_VERSION
    assert (
        contract["framework_serialization"]
        == MongoDBCheckpointStorage.FRAMEWORK_SERIALIZATION_VERSION
    )
    assert contract["idempotency_hash_version"] == MongoDBCheckpointStorage.IDEMPOTENCY_HASH_VERSION
    assert contract["payload_versions"] == sorted(
        MongoDBCheckpointStorage.SUPPORTED_PAYLOAD_VERSIONS
    )
    assert contract["collection_default"] == MongoDBCheckpointStorage.DEFAULT_COLLECTION_NAME
    defaults = MongoDBCheckpointStorageOptions(
        tenant_id="tenant",
        workflow_name="workflow",
        session_id="session",
    )
    assert contract["pagination"]["default_page_size"] == defaults.page_size
    assert contract["pagination"]["maximum_page_size"] == defaults.max_page_size
    assert contract["pagination"]["inherited_lists"] == "all_records_via_bounded_pages"
    assert contract["retention"]["counter_expiration_is_refreshed"]
    assert contract["retention"]["counter_expiration_update"] == "atomic_max"
    assert (
        contract["retention"]["missing_counter_recovery"]
        == "atomic_max_of_counter_and_retained_sequence"
    )
    assert contract["retention"]["permanent_checkpoint_disables_counter_expiration"]
    assert not contract["retention"]["ttl_deletion_order_dependency"]
    assert contract["retention"]["authorized_clear_run_deletes_counter"]
    assert contract["canonical_mappings"] == {
        "dict_order": "insensitive",
        "ordered_dict_order": "sensitive_with_type_tag",
        "ordered_dict_reduction": "exact_entries_without_additional_fields",
        "allowlisted_mapping_subclass": "reject_with_serialization_error",
        "instance_state_beyond_entries": "reject_with_serialization_error",
        "unsupported_subclass": "reject_with_migration_guidance",
    }
    assert [item["name"] for item in contract["indexes"]] == [
        "checkpoint_scope_identity",
        "checkpoint_scope_sequence",
        "checkpoint_scope_lineage",
        "checkpoint_expiration",
        "checkpoint_counter_expiration",
    ]
