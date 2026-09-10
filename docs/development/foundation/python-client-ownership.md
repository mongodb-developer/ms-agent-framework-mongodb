# Python package, client ownership, and lifecycle

This document describes the Python portion of implementation-map slice 1,
[Foundation and shared internals](../../spec/implementation-map.md). It implements the package
identity and resource-lifecycle requirements from the
[package specification](../../spec/packages.md) and
[system architecture](../../spec/architecture/system.md). The ownership model follows
[ADR 0005](../../decisions/0005-fix-resource-ownership-at-construction.md); the specification
remains authoritative while that ADR is proposed.

## Package surface

The distribution is built from `python/pyproject.toml` as `agent-framework-mongodb`. Its import
root is `agent_framework_mongodb`, and new MongoDB access uses PyMongo's asynchronous client.
Feature providers are intentionally absent from this foundation slice.

Stable integration errors are exported from `python/src/agent_framework_mongodb/__init__.py`.
Feature branches extend this taxonomy without exposing PyMongo implementation details as public
configuration.

## Client construction and ownership

`MongoClientHandle` in
`python/src/agent_framework_mongodb/_shared/client.py` is the internal ownership boundary.

- `MongoClientHandle.from_uri(...)` validates the URI before constructing an
  `AsyncMongoClient` and permanently records that the integration owns it.
- `MongoClientHandle.from_client(...)` records an injected client as caller-owned.
- `close()` closes an owned client at most once and supports the synchronous and awaitable close
  contracts accepted by the installed PyMongo compatibility range.
- `close()` never closes an injected client.
- The async context manager delegates to `close()` even when the managed operation raises.

Ownership is immutable after construction. Feature providers must retain the handle rather than
copying its client and must delegate their async cleanup to it. Databases and collections obtained
from an injected client remain caller-owned.

## Validation and failure behavior

An empty or whitespace-only connection URI raises `MongoDBConfigurationError` before a client
factory is invoked. Driver construction errors are not caught or converted at this boundary, so
their original type and traceback remain available.

The package does not log connection strings and does not read credentials from environment
variables. Samples and integration tests will consume the environment variables defined by the
[configuration specification](../../spec/configuration.md).

## Verification

`python/tests/unit/test_client.py` covers:

- owned synchronous and awaitable client cleanup;
- idempotent cleanup;
- non-disposal of injected clients;
- cleanup after a context-manager failure; and
- validation before client construction.

Validated commands for this slice:

```powershell
python -m pytest tests\unit\test_client.py
python -m ruff check src\agent_framework_mongodb\_shared\client.py `
  src\agent_framework_mongodb\errors.py tests\unit\test_client.py
```

The complete foundation quality gate also runs mypy, Pyright, package builds, and clean artifact
installation smoke tests after all shared mechanics are present.
