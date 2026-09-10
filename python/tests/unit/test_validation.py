from math import inf, nan
from re import escape

import pytest

from agent_framework_mongodb import (
    MongoDBCapabilityError,
    MongoDBConfigurationError,
    MongoDBEmbeddingError,
    MongoDBMappingError,
)
from agent_framework_mongodb._shared.capabilities import CapabilityResult
from agent_framework_mongodb._shared.embeddings import normalize_embeddings, validate_dimensions
from agent_framework_mongodb._shared.field_paths import resolve_field_path, validate_field_path


@pytest.mark.parametrize(
    ("path", "message"),
    [
        ("", "must not be empty"),
        ("source..title", "empty segments"),
        ("$source.title", "'$' field segments"),
        ("source.0.title", "positional array syntax"),
        ("source\x00.title", "null bytes"),
        ("metadata._ragScore", "reserved alias"),
    ],
)
def test_invalid_field_paths_are_rejected(path: str, message: str) -> None:
    with pytest.raises(MongoDBConfigurationError, match=escape(message)):
        validate_field_path(path, option_name="title_field")


def test_nested_field_path_is_resolved() -> None:
    assert resolve_field_path({"source": {"title": "Guide"}}, "source.title") == "Guide"


def test_missing_nested_field_is_a_mapping_error() -> None:
    with pytest.raises(MongoDBMappingError, match="source.title"):
        resolve_field_path({"source": {}}, "source.title")


@pytest.mark.parametrize("dimensions", [0, -1, True])
def test_invalid_dimensions_are_rejected(dimensions: int) -> None:
    with pytest.raises(MongoDBConfigurationError, match="positive integer"):
        validate_dimensions(dimensions)


def test_embeddings_are_normalized() -> None:
    assert normalize_embeddings([[1, 2.5]], expected_count=1, dimensions=2) == ((1.0, 2.5),)


def test_embedding_count_must_match() -> None:
    with pytest.raises(MongoDBEmbeddingError, match="expected 2"):
        normalize_embeddings([[1.0]], expected_count=2, dimensions=1)


def test_embedding_dimensions_must_match() -> None:
    with pytest.raises(MongoDBEmbeddingError, match="expected 2"):
        normalize_embeddings([[1.0]], expected_count=1, dimensions=2)


@pytest.mark.parametrize("value", [True, "1", None])
def test_embedding_values_must_be_numeric(value: object) -> None:
    with pytest.raises(MongoDBEmbeddingError, match="must be numeric"):
        normalize_embeddings([[value]], expected_count=1, dimensions=1)


@pytest.mark.parametrize("value", [nan, inf, -inf])
def test_embedding_values_must_be_finite(value: float) -> None:
    with pytest.raises(MongoDBEmbeddingError, match="must be finite"):
        normalize_embeddings([[value]], expected_count=1, dimensions=1)


def test_unsupported_capability_has_actionable_error() -> None:
    capability = CapabilityResult(
        "hybrid RRF",
        supported=False,
        remediation="Use MongoDB 8.0 or later with $rankFusion enabled.",
        detected_values={"server_version": "7.0"},
    )

    with pytest.raises(MongoDBCapabilityError, match="MongoDB 8.0"):
        capability.require()


def test_capability_detected_values_are_immutable() -> None:
    detected_values = {"server_version": "8.0"}
    capability = CapabilityResult("hybrid RRF", True, detected_values=detected_values)
    detected_values["server_version"] = "7.0"

    assert capability.detected_values == {"server_version": "8.0"}
