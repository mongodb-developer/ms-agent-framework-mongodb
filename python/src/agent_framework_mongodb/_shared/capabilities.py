"""Immutable MongoDB capability evaluation results."""

from collections.abc import Mapping
from dataclasses import dataclass
from types import MappingProxyType

from ..errors import MongoDBCapabilityError


@dataclass(frozen=True, slots=True)
class CapabilityResult:
    name: str
    supported: bool
    remediation: str | None = None
    detected_values: Mapping[str, str] | None = None

    def __post_init__(self) -> None:
        if not self.name.strip():
            raise ValueError("Capability name must not be empty.")
        if not self.supported and not self.remediation:
            raise ValueError("Unsupported capabilities require remediation guidance.")
        if self.detected_values is not None:
            object.__setattr__(
                self, "detected_values", MappingProxyType(dict(self.detected_values))
            )

    def require(self) -> None:
        if not self.supported:
            raise MongoDBCapabilityError(
                f"MongoDB capability '{self.name}' is unavailable. {self.remediation}"
            )
