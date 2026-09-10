"""Validation and safe resolution of configured MongoDB field paths."""

from __future__ import annotations

from collections.abc import Mapping
from typing import Final, cast

from ..errors import MongoDBConfigurationError, MongoDBMappingError

_RESERVED_ALIASES: Final = frozenset({"_ragScore"})


def validate_field_path(path: str, *, option_name: str = "field path") -> str:
    """Return a valid configured path or raise a configuration error."""
    if not path:
        raise MongoDBConfigurationError(f"{option_name} must not be empty.")
    if "\x00" in path:
        raise MongoDBConfigurationError(f"{option_name} must not contain null bytes.")

    segments = path.split(".")
    if any(not segment for segment in segments):
        raise MongoDBConfigurationError(f"{option_name} must not contain empty segments.")
    if any(segment.startswith("$") for segment in segments):
        raise MongoDBConfigurationError(f"{option_name} must not contain '$' field segments.")
    if any(segment.isdecimal() or segment == "$[]" for segment in segments):
        raise MongoDBConfigurationError(f"{option_name} must not use positional array syntax.")
    if any(segment in _RESERVED_ALIASES for segment in segments):
        raise MongoDBConfigurationError(
            f"{option_name} must not collide with reserved alias '_ragScore'."
        )

    return path


def resolve_field_path(document: Mapping[str, object], path: str) -> object:
    """Resolve a previously configured path without evaluating dynamic code."""
    validate_field_path(path)
    current: object = document
    for segment in path.split("."):
        if not isinstance(current, Mapping) or segment not in current:
            raise MongoDBMappingError(f"Required field '{path}' is missing from the result.")
        current = cast(Mapping[object, object], current)[segment]
    return current
