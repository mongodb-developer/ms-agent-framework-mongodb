from __future__ import annotations

import json
from pathlib import Path
from typing import Any, cast

import pytest

from agent_framework_mongodb import (
    AndFilter,
    EqualFilter,
    GreaterThanOrEqualFilter,
    InFilter,
    MongoDBConfigurationError,
    MongoDBFilter,
    MongoDBRAGProviderOptions,
    MongoDBRAGResult,
    MongoDBSearchMode,
    NotInFilter,
    OrFilter,
)
from agent_framework_mongodb.rag._filters import compile_filter


def _fixture() -> dict[str, Any]:
    path = Path(__file__).parents[3] / "tests" / "fixtures" / "rag" / "contracts.json"
    return cast(dict[str, Any], json.loads(path.read_text(encoding="utf-8")))


def _filter(value: dict[str, Any]) -> MongoDBFilter:
    operator = value["operator"]
    if operator == "eq":
        return EqualFilter(value["field"], value["value"])
    if operator == "in":
        return InFilter(value["field"], tuple(value["values"]))
    if operator == "not_in":
        return NotInFilter(value["field"], tuple(value["values"]))
    if operator == "gte":
        return GreaterThanOrEqualFilter(value["field"], value["value"])
    children = tuple(_filter(child) for child in value["filters"])
    if operator == "and":
        return AndFilter(*children)
    if operator == "or":
        return OrFilter(*children)
    raise AssertionError(f"Fixture contains unknown operator {operator!r}.")


def test_language_neutral_option_contract() -> None:
    for case in _fixture()["option_cases"]:
        if case["valid"]:
            options = MongoDBRAGProviderOptions(**case["input"])
            normalized = case["normalized"]
            assert options.mode.value == normalized["mode"], case["name"]
            assert options.top_k == normalized["top_k"], case["name"]
            assert options.num_candidates == normalized["num_candidates"], case["name"]
        else:
            with pytest.raises(
                MongoDBConfigurationError,
                match=case["error_contains"],
            ):
                MongoDBRAGProviderOptions(**case["input"])


def test_language_neutral_filter_translation_contract() -> None:
    for case in _fixture()["filter_cases"]:
        expression = _filter(case["ast"])
        assert compile_filter(expression, MongoDBSearchMode.VECTOR_ANN) == case["vector"]
        assert compile_filter(expression, MongoDBSearchMode.VECTOR_ENN) == case["vector"]
        assert compile_filter(expression, MongoDBSearchMode.FULL_TEXT) == case["search"]
        assert compile_filter(expression, MongoDBSearchMode.HYBRID_RRF) == {
            "vector": case["vector"],
            "search": case["search"],
        }


def test_language_neutral_result_and_citation_contract() -> None:
    fixture = _fixture()["result"]
    result = MongoDBRAGResult(**fixture["input"])

    assert {
        "id": result.id,
        "text": result.text,
        "source_name": result.source_name,
        "source_url": result.source_url,
        "score": result.score,
        "metadata": dict(result.metadata),
    } == fixture["normalized"]
    citation = dict(result.to_citation())
    citation.pop("raw_representation")
    assert citation == fixture["citation"]
    assert result.raw_document is fixture["input"]["raw_document"]


def test_language_neutral_security_contract_is_explicit() -> None:
    contract = _fixture()["security_contract"]

    assert set(contract["filter_placement"]) == {
        "vector_ann",
        "vector_enn",
        "full_text",
        "hybrid_rrf",
    }
    assert contract["runtime_operations"] == ["aggregate"]
    assert contract["cancellation"] == "propagate"
    assert contract["partial_filter_translation"] == "reject"
