"""Embedding validation shared by Memory and RAG."""

from __future__ import annotations

from collections.abc import Sequence
from math import isfinite
from numbers import Real

from ..errors import MongoDBConfigurationError, MongoDBEmbeddingError


def validate_dimensions(dimensions: object) -> int:
    if not isinstance(dimensions, int) or isinstance(dimensions, bool) or dimensions <= 0:
        raise MongoDBConfigurationError("vector_dimensions must be a positive integer.")
    return dimensions


def normalize_embeddings(
    embeddings: Sequence[Sequence[object]],
    *,
    expected_count: int,
    dimensions: int,
) -> tuple[tuple[float, ...], ...]:
    """Validate embedding count, dimensions, and finite numeric values."""
    validate_dimensions(dimensions)
    if expected_count < 0:
        raise MongoDBConfigurationError("Expected embedding count must not be negative.")
    if len(embeddings) != expected_count:
        raise MongoDBEmbeddingError(
            f"Embedding generator returned {len(embeddings)} vectors; expected {expected_count}."
        )

    normalized: list[tuple[float, ...]] = []
    for vector_index, vector in enumerate(embeddings):
        if len(vector) != dimensions:
            raise MongoDBEmbeddingError(
                f"Embedding {vector_index} has {len(vector)} dimensions; expected {dimensions}."
            )

        normalized_vector: list[float] = []
        for value_index, value in enumerate(vector):
            if isinstance(value, bool) or not isinstance(value, Real):
                raise MongoDBEmbeddingError(
                    f"Embedding {vector_index} value {value_index} must be numeric."
                )
            normalized_value = float(value)
            if not isfinite(normalized_value):
                raise MongoDBEmbeddingError(
                    f"Embedding {vector_index} value {value_index} must be finite."
                )
            normalized_vector.append(normalized_value)
        normalized.append(tuple(normalized_vector))

    return tuple(normalized)
