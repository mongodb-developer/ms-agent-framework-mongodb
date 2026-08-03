"""Compare the installed public API with the reviewed release baseline."""

from __future__ import annotations

import argparse
import inspect
import json
from pathlib import Path
from types import ModuleType
from typing import Any, cast

import agent_framework_mongodb


def _signature(value: object | None) -> str | None:
    if value is None:
        return None
    try:
        return str(inspect.signature(value))
    except (TypeError, ValueError):
        return None


def _is_package_owned(value: object, package_prefix: str) -> bool:
    module = getattr(value, "__module__", "")
    return module == package_prefix or module.startswith(f"{package_prefix}.")


def _defining_class(value: type[Any], name: str) -> type[Any] | None:
    return next((base for base in value.__mro__ if name in vars(base)), None)


def _property_surface(value: property) -> dict[str, str]:
    surface = {"kind": "property"}
    for name, accessor in (
        ("getter", value.fget),
        ("setter", value.fset),
        ("deleter", value.fdel),
    ):
        if signature := _signature(accessor):
            surface[name] = signature
    return surface


def _class_surface(value: type[Any], package_prefix: str) -> dict[str, Any]:
    constructor_owner = _defining_class(value, "__init__")
    constructor = (
        _signature(value)
        if constructor_owner is not None and _is_package_owned(constructor_owner, package_prefix)
        else None
    )
    members: dict[str, dict[str, str]] = {}
    for name in dir(value):
        if name.startswith("_"):
            continue
        owner = _defining_class(value, name)
        if owner is None or not _is_package_owned(owner, package_prefix):
            continue
        descriptor = vars(owner)[name]
        if isinstance(descriptor, property):
            members[name] = _property_surface(descriptor)
        elif isinstance(descriptor, classmethod):
            signature = _signature(getattr(value, name))
            if signature is not None:
                members[name] = {"kind": "classmethod", "signature": signature}
        elif isinstance(descriptor, staticmethod):
            signature = _signature(descriptor.__func__)
            if signature is not None:
                members[name] = {"kind": "staticmethod", "signature": signature}
        elif inspect.isfunction(descriptor):
            signature = _signature(descriptor)
            if signature is not None:
                members[name] = {"kind": "method", "signature": signature}
    return {"constructor": constructor, "members": members}


def snapshot_public_api(package: ModuleType) -> dict[str, Any]:
    exports = sorted(cast(list[str], package.__all__))
    package_prefix = package.__name__
    classes: dict[str, dict[str, Any]] = {}
    callables: dict[str, str] = {}
    for name in exports:
        value = getattr(package, name)
        if inspect.isclass(value) and _is_package_owned(value, package_prefix):
            classes[name] = _class_surface(value, package_prefix)
        elif inspect.isfunction(value) and _is_package_owned(value, package_prefix):
            if signature := _signature(value):
                callables[name] = signature
    return {
        "exports": exports,
        "callables": callables,
        "classes": classes,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument(
        "--write",
        action="store_true",
        help="replace the baseline after an intentional versioned API review",
    )
    args = parser.parse_args()
    current = {
        "baseline_version": agent_framework_mongodb.__version__,
        **snapshot_public_api(agent_framework_mongodb),
    }

    if args.write:
        args.baseline.write_text(
            json.dumps(current, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        return 0

    expected = json.loads(args.baseline.read_text(encoding="utf-8"))
    if expected.get("baseline_version") != agent_framework_mongodb.__version__:
        print(
            f"API baseline version {expected.get('baseline_version')} does not match installed "
            f"version {agent_framework_mongodb.__version__}."
        )
        return 1
    if current != expected:
        print("Public API differs from the reviewed baseline.")
        print(
            json.dumps(
                {"expected": expected, "current": current},
                indent=2,
                sort_keys=True,
            )
        )
        return 1
    print(f"Public API matches baseline {expected['baseline_version']}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
