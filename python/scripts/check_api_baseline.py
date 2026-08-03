"""Compare the installed public API with the reviewed release baseline."""

from __future__ import annotations

import argparse
import inspect
import json
from pathlib import Path
from typing import Any

import agent_framework_mongodb


def _signature(value: object) -> str | None:
    if inspect.isclass(value):
        if "__init__" not in vars(value):
            return None
    elif not inspect.isfunction(value):
        return None
    try:
        return str(inspect.signature(value))
    except (TypeError, ValueError):
        return None


def _current_api() -> dict[str, Any]:
    exports = sorted(agent_framework_mongodb.__all__)
    signatures = {
        name: signature
        for name in exports
        if (signature := _signature(getattr(agent_framework_mongodb, name))) is not None
    }
    return {
        "exports": exports,
        "signatures": signatures,
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
    current = _current_api()

    if args.write:
        current = {
            "baseline_version": agent_framework_mongodb.__version__,
            **current,
        }
        args.baseline.write_text(
            json.dumps(current, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        return 0

    expected = json.loads(args.baseline.read_text(encoding="utf-8"))
    expected_api = {
        "exports": expected["exports"],
        "signatures": expected["signatures"],
    }
    if current != expected_api:
        print("Public API differs from the reviewed baseline.")
        print(
            json.dumps(
                {"expected": expected_api, "current": current},
                indent=2,
                sort_keys=True,
            )
        )
        return 1
    print(f"Public API matches baseline {expected['baseline_version']}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
