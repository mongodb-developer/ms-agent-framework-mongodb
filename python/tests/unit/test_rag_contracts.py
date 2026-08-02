from collections.abc import Mapping
from typing import Any

import pytest
from agent_framework import Annotation, ContextProvider

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBCapabilityError,
    MongoDBConfigurationError,
    MongoDBRAGContextProvider,
    MongoDBRAGParentOptions,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBRAGResult,
    MongoDBRAGSearchOptions,
    MongoDBSearchMode,
)


def ann_options(**changes: Any) -> MongoDBRAGProviderOptions:
    values: dict[str, Any] = {
        "mode": MongoDBSearchMode.VECTOR_ANN,
        "vector_dimensions": 3,
        "vector_index_name": "knowledge_vector",
    }
    values.update(changes)
    return MongoDBRAGProviderOptions(**values)


@pytest.mark.parametrize(
    ("changes", "message"),
    [
        ({"top_k": 0}, "top_k"),
        ({"top_k": 101}, "top_k"),
        ({"num_candidates": 4, "top_k": 5}, "at least top_k"),
        ({"num_candidates": 10_001}, "num_candidates"),
        ({"vector_dimensions": 0}, "dimensions"),
        ({"vector_field": "$embedding"}, "vector_field"),
        ({"vector_index_name": "bad index!"}, "vector_index_name"),
        ({"text_fields": ()}, "text_fields"),
    ],
)
def test_provider_options_validate_bounds_and_names(changes: dict[str, Any], message: str) -> None:
    with pytest.raises(MongoDBConfigurationError, match=message):
        ann_options(**changes)


def test_ann_options_normalize_sequences_and_defaults() -> None:
    options = ann_options(
        text_fields=["content", "summary", "content"],
        metadata_fields=["metadata.kind", "metadata.kind"],
    )

    assert options.mode is MongoDBSearchMode.VECTOR_ANN
    assert options.text_fields == ("content", "summary")
    assert options.metadata_fields == ("metadata.kind",)
    assert options.top_k == 5
    assert options.num_candidates == 50


@pytest.mark.parametrize("option_name", ["text_fields", "metadata_fields"])
@pytest.mark.parametrize("value", ["content", b"content"])
def test_sequence_valued_field_options_reject_scalar_strings_and_bytes(
    option_name: str,
    value: object,
) -> None:
    with pytest.raises(MongoDBConfigurationError, match="explicit list or tuple"):
        ann_options(**{option_name: value})


@pytest.mark.parametrize("option_name", ["text_fields", "metadata_fields"])
def test_sequence_valued_field_options_reject_non_sequence_iterables(
    option_name: str,
) -> None:
    with pytest.raises(MongoDBConfigurationError, match="explicit list or tuple"):
        ann_options(**{option_name: iter(("content",))})


def test_text_fields_must_remain_non_empty_after_normalization() -> None:
    with pytest.raises(MongoDBConfigurationError, match="at least one"):
        ann_options(text_fields=[])


def test_enn_forbids_candidates_and_search_index() -> None:
    with pytest.raises(MongoDBConfigurationError, match="num_candidates"):
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ENN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            num_candidates=20,
        )

    with pytest.raises(MongoDBConfigurationError, match="search_index_name"):
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ENN,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            search_index_name="knowledge_text",
        )


def test_full_text_forbids_vector_only_options() -> None:
    with pytest.raises(MongoDBConfigurationError, match="vector_dimensions"):
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.FULL_TEXT,
            search_index_name="knowledge_text",
            vector_dimensions=3,
        )


def test_hybrid_requires_both_indexes_and_valid_weights() -> None:
    with pytest.raises(MongoDBConfigurationError, match="search_index_name"):
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.HYBRID_RRF,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
        )

    with pytest.raises(MongoDBConfigurationError, match="at least one"):
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.HYBRID_RRF,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            search_index_name="knowledge_text",
            vector_weight=0,
            text_weight=0,
        )


def test_search_options_normalize_against_provider_without_mutating_mandatory_filter() -> None:
    mandatory = EqualFilter("tenant_id", "tenant-a")
    provider_options = ann_options(filter=mandatory)

    normalized = provider_options.normalize_search_options(
        MongoDBRAGSearchOptions(top_k=10, num_candidates=100)
    )

    assert normalized.top_k == 10
    assert normalized.num_candidates == 100
    assert normalized.filter is mandatory


def test_search_options_cannot_replace_application_filter() -> None:
    with pytest.raises(TypeError):
        MongoDBRAGSearchOptions(filter={"tenant_id": "tenant-b"})  # type: ignore[arg-type]


def test_parent_options_validate_same_database_lookup_and_bounds() -> None:
    parent = MongoDBRAGParentOptions(
        collection_name="knowledge_parents",
        parent_id_field="parent_id",
        max_parents=8,
        max_parent_text_length=20_000,
        max_lookup_fan_out=16,
        max_context_tokens=4_000,
    )

    assert parent.collection_name == "knowledge_parents"

    with pytest.raises(MongoDBConfigurationError, match="same-database"):
        MongoDBRAGParentOptions(collection_name="other_db.parents")

    with pytest.raises(MongoDBConfigurationError, match="max_lookup_fan_out"):
        MongoDBRAGParentOptions(max_lookup_fan_out=0)


def test_parent_retrieval_is_rejected_for_non_vector_mode() -> None:
    with pytest.raises(MongoDBConfigurationError, match="parent retrieval"):
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.FULL_TEXT,
            search_index_name="knowledge_text",
            parent=MongoDBRAGParentOptions(),
        )


def test_result_preserves_raw_document_and_normalized_semantics() -> None:
    raw: Mapping[str, object] = {"_id": "doc-1", "content": "MongoDB guide", "private": 42}
    result = MongoDBRAGResult(
        id="doc-1",
        text="MongoDB guide",
        source_name="Guide",
        source_url="https://example.test/guide",
        score=0.82,
        metadata={"kind": "documentation"},
        raw_document=raw,
    )

    assert result.raw_document is raw
    assert result.metadata == {"kind": "documentation"}
    assert result.score == 0.82


def test_result_converts_to_framework_citation_without_losing_raw_result() -> None:
    result = MongoDBRAGResult(
        id="doc-1",
        text="MongoDB guide",
        source_name="Guide",
        source_url="https://example.test/guide",
        score=0.82,
        metadata={"kind": "documentation"},
        raw_document={"_id": "doc-1"},
    )

    citation: Annotation = result.to_citation()

    assert citation.get("type") == "citation"
    assert citation.get("title") == "Guide"
    assert citation.get("url") == "https://example.test/guide"
    assert citation.get("snippet") == "MongoDB guide"
    assert citation.get("raw_representation") is result
    assert citation.get("additional_properties") == {
        "document_id": "doc-1",
        "score": 0.82,
        "metadata": {"kind": "documentation"},
    }


@pytest.mark.parametrize(
    ("changes", "message"),
    [
        ({"id": ""}, "id"),
        ({"text": ""}, "text"),
        ({"score": float("inf")}, "score"),
        ({"metadata": []}, "metadata"),
        ({"raw_document": []}, "raw_document"),
    ],
)
def test_result_rejects_invalid_mapping_inputs(changes: dict[str, Any], message: str) -> None:
    values: dict[str, Any] = {
        "id": "doc-1",
        "text": "text",
        "score": 1.0,
        "metadata": {},
        "raw_document": {},
    }
    values.update(changes)
    with pytest.raises(MongoDBConfigurationError, match=message):
        MongoDBRAGResult(**values)


def test_provider_contracts_are_public_and_do_not_execute_unimplemented_modes() -> None:
    provider = MongoDBRAGProvider(ann_options())
    context_provider = MongoDBRAGContextProvider(provider)

    assert isinstance(context_provider, ContextProvider)
    assert context_provider.provider is provider


async def test_direct_search_fails_clearly_until_mode_implementation_is_installed() -> None:
    provider = MongoDBRAGProvider(ann_options())

    with pytest.raises(MongoDBCapabilityError, match="not installed"):
        await provider.search("query")
