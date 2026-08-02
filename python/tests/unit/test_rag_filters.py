from collections.abc import Callable
from datetime import datetime, timezone
from math import inf
from typing import Any, cast

import pytest

from agent_framework_mongodb import (
    AndFilter,
    EqualFilter,
    GreaterThanFilter,
    GreaterThanOrEqualFilter,
    InFilter,
    LessThanFilter,
    LessThanOrEqualFilter,
    MongoDBConfigurationError,
    MongoDBFilter,
    NotEqualFilter,
    NotInFilter,
    OrFilter,
)
from agent_framework_mongodb.rag._filters import compile_filter
from agent_framework_mongodb.rag.options import MongoDBSearchMode

_OUT_OF_RANGE_FACTORIES: tuple[Callable[[int], MongoDBFilter], ...] = (
    lambda value: EqualFilter("value", value),
    lambda value: NotEqualFilter("value", value),
    lambda value: InFilter("value", [value]),
    lambda value: NotInFilter("value", (value,)),
    lambda value: GreaterThanFilter("value", value),
    lambda value: LessThanOrEqualFilter("value", value),
)


def test_filter_ast_supports_required_operator_surface() -> None:
    created = datetime(2026, 1, 1, tzinfo=timezone.utc)
    expression = AndFilter(
        EqualFilter("tenant_id", "tenant-a"),
        NotEqualFilter("status", "deleted"),
        InFilter("category", ("guide", "reference")),
        NotInFilter("region", ("blocked",)),
        GreaterThanFilter("rank", 1),
        GreaterThanOrEqualFilter("created_at", created),
        LessThanFilter("rank", 100),
        LessThanOrEqualFilter("created_at", created),
    )

    assert isinstance(expression, MongoDBFilter)


@pytest.mark.parametrize("filter_type", [InFilter, NotInFilter])
@pytest.mark.parametrize("values", ["tenant-a", b"tenant-a"])
def test_membership_filters_reject_scalar_string_and_bytes(
    filter_type: type[InFilter],
    values: object,
) -> None:
    with pytest.raises(MongoDBConfigurationError, match="explicit list or tuple"):
        filter_type("tenant_id", cast(Any, values))


@pytest.mark.parametrize("filter_type", [InFilter, NotInFilter])
def test_membership_filters_accept_and_normalize_explicit_lists(
    filter_type: type[InFilter],
) -> None:
    expression = filter_type("tenant_id", ["tenant-a", "tenant-b"])

    assert expression.values == ("tenant-a", "tenant-b")


@pytest.mark.parametrize("value", [-(2**63), 2**63 - 1])
def test_filter_integer_values_accept_bson_int64_boundaries(value: int) -> None:
    assert EqualFilter("value", value).value == value
    assert InFilter("value", [value]).values == (value,)
    assert GreaterThanOrEqualFilter("value", value).value == value


@pytest.mark.parametrize("value", [-(2**63) - 1, 2**63])
@pytest.mark.parametrize(
    "factory",
    _OUT_OF_RANGE_FACTORIES,
)
def test_filter_integer_values_reject_outside_bson_int64(
    factory: Callable[[int], MongoDBFilter],
    value: int,
) -> None:
    with pytest.raises(MongoDBConfigurationError, match="BSON int64 range"):
        factory(value)


@pytest.mark.parametrize(
    "factory",
    [
        lambda: GreaterThanFilter("value", True),
        lambda: LessThanOrEqualFilter("value", False),
    ],
)
def test_range_filters_reject_boolean_numeric_values(factory: Any) -> None:
    with pytest.raises(MongoDBConfigurationError, match="numeric or datetime"):
        factory()


@pytest.mark.parametrize(
    ("factory", "message"),
    [
        (lambda: EqualFilter("$tenant", "a"), "field"),
        (lambda: EqualFilter("tenant", cast(Any, {"$ne": "a"})), "scalar"),
        (lambda: EqualFilter("score", inf), "finite"),
        (lambda: InFilter("tenant", ()), "at least one"),
        (lambda: InFilter("tenant", tuple(range(101))), "at most 100"),
        (lambda: GreaterThanFilter("rank", cast(Any, "high")), "numeric or datetime"),
        (lambda: AndFilter(EqualFilter("a", 1)), "at least two"),
    ],
)
def test_invalid_filter_inputs_fail_closed(factory: object, message: str) -> None:
    with pytest.raises(MongoDBConfigurationError, match=message):
        factory()  # type: ignore[operator]


def test_boolean_filter_depth_is_bounded() -> None:
    expression: MongoDBFilter = EqualFilter("tenant", "a")
    for index in range(7):
        expression = AndFilter(expression, EqualFilter(f"scope{index}", index))

    with pytest.raises(MongoDBConfigurationError, match="nesting depth"):
        AndFilter(expression, EqualFilter("too_deep", True))


def test_vector_translation_is_complete_and_structured() -> None:
    expression = AndFilter(
        EqualFilter("tenant_id", "tenant-a"),
        OrFilter(
            InFilter("kind", ("guide", "reference")),
            GreaterThanOrEqualFilter("published_year", 2025),
        ),
        NotEqualFilter("status", "deleted"),
    )

    assert compile_filter(expression, MongoDBSearchMode.VECTOR_ANN) == {
        "$and": [
            {"tenant_id": {"$eq": "tenant-a"}},
            {
                "$or": [
                    {"kind": {"$in": ["guide", "reference"]}},
                    {"published_year": {"$gte": 2025}},
                ]
            },
            {"status": {"$ne": "deleted"}},
        ]
    }


def test_search_translation_is_complete_and_structured() -> None:
    expression = AndFilter(
        EqualFilter("tenant_id", "tenant-a"),
        OrFilter(
            LessThanFilter("rank", 10),
            NotInFilter("status", ("deleted", "hidden")),
        ),
    )

    assert compile_filter(expression, MongoDBSearchMode.FULL_TEXT) == [
        {"equals": {"path": "tenant_id", "value": "tenant-a"}},
        {
            "compound": {
                "should": [
                    {"range": {"path": "rank", "lt": 10}},
                    {
                        "compound": {
                            "mustNot": [
                                {
                                    "in": {
                                        "path": "status",
                                        "value": ["deleted", "hidden"],
                                    }
                                }
                            ]
                        }
                    },
                ],
                "minimumShouldMatch": 1,
            }
        },
    ]


def test_hybrid_translation_covers_both_branches() -> None:
    compiled = compile_filter(EqualFilter("tenant_id", "tenant-a"), MongoDBSearchMode.HYBRID_RRF)

    assert compiled == {
        "vector": {"tenant_id": {"$eq": "tenant-a"}},
        "search": [{"equals": {"path": "tenant_id", "value": "tenant-a"}}],
    }
