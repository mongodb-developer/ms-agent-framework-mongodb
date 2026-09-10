"""Complete structured translators for mandatory RAG filters."""

from __future__ import annotations

from typing import Any

from ..errors import MongoDBFilterTranslationError
from .filters import (
    AndFilter,
    EqualFilter,
    GreaterThanFilter,
    GreaterThanOrEqualFilter,
    InFilter,
    LessThanFilter,
    LessThanOrEqualFilter,
    MongoDBFilter,
    NotEqualFilter,
    NotInFilter,
    OrFilter,
)
from .options import MongoDBSearchMode

MongoDocument = dict[str, Any]


def _vector(expression: MongoDBFilter) -> MongoDocument:
    if isinstance(expression, EqualFilter):
        return {expression.field: {"$eq": expression.value}}
    if isinstance(expression, NotEqualFilter):
        return {expression.field: {"$ne": expression.value}}
    if isinstance(expression, NotInFilter):
        return {expression.field: {"$nin": list(expression.values)}}
    if isinstance(expression, InFilter):
        return {expression.field: {"$in": list(expression.values)}}
    if isinstance(expression, GreaterThanFilter):
        return {expression.field: {"$gt": expression.value}}
    if isinstance(expression, GreaterThanOrEqualFilter):
        return {expression.field: {"$gte": expression.value}}
    if isinstance(expression, LessThanFilter):
        return {expression.field: {"$lt": expression.value}}
    if isinstance(expression, LessThanOrEqualFilter):
        return {expression.field: {"$lte": expression.value}}
    if isinstance(expression, AndFilter):
        return {"$and": [_vector(child) for child in expression.filters]}
    if isinstance(expression, OrFilter):
        return {"$or": [_vector(child) for child in expression.filters]}
    raise MongoDBFilterTranslationError(
        f"Filter type {type(expression).__name__!r} is unsupported for Vector Search."
    )


def _search(expression: MongoDBFilter) -> MongoDocument:
    if isinstance(expression, EqualFilter):
        return {"equals": {"path": expression.field, "value": expression.value}}
    if isinstance(expression, NotEqualFilter):
        return {
            "compound": {
                "mustNot": [{"equals": {"path": expression.field, "value": expression.value}}]
            }
        }
    if isinstance(expression, NotInFilter):
        return {
            "compound": {
                "mustNot": [{"in": {"path": expression.field, "value": list(expression.values)}}]
            }
        }
    if isinstance(expression, InFilter):
        return {"in": {"path": expression.field, "value": list(expression.values)}}
    if isinstance(expression, GreaterThanFilter):
        return {"range": {"path": expression.field, "gt": expression.value}}
    if isinstance(expression, GreaterThanOrEqualFilter):
        return {"range": {"path": expression.field, "gte": expression.value}}
    if isinstance(expression, LessThanFilter):
        return {"range": {"path": expression.field, "lt": expression.value}}
    if isinstance(expression, LessThanOrEqualFilter):
        return {"range": {"path": expression.field, "lte": expression.value}}
    if isinstance(expression, AndFilter):
        return {"compound": {"filter": [_search(child) for child in expression.filters]}}
    if isinstance(expression, OrFilter):
        return {
            "compound": {
                "should": [_search(child) for child in expression.filters],
                "minimumShouldMatch": 1,
            }
        }
    raise MongoDBFilterTranslationError(
        f"Filter type {type(expression).__name__!r} is unsupported for MongoDB Search."
    )


def compile_filter(
    expression: MongoDBFilter,
    mode: MongoDBSearchMode,
) -> MongoDocument | list[MongoDocument]:
    """Compile one complete mandatory filter for every active retrieval branch."""
    if mode in (MongoDBSearchMode.VECTOR_ANN, MongoDBSearchMode.VECTOR_ENN):
        return _vector(expression)
    if mode is MongoDBSearchMode.FULL_TEXT:
        if isinstance(expression, AndFilter):
            return [_search(child) for child in expression.filters]
        return [_search(expression)]
    if mode is MongoDBSearchMode.HYBRID_RRF:
        search = (
            [_search(child) for child in expression.filters]
            if isinstance(expression, AndFilter)
            else [_search(expression)]
        )
        return {"vector": _vector(expression), "search": search}
    raise MongoDBFilterTranslationError(f"Search mode {mode!r} cannot translate filters.")


def compile_match_filter(expression: MongoDBFilter) -> MongoDocument:
    """Compile a complete typed filter for an authorized post-retrieval read."""
    return _vector(expression)
