# Python shared validation mechanics

This document describes the Python validation portion of implementation-map slice 1,
[Foundation and shared internals](../../spec/implementation-map.md). The implementation follows
the validation and error requirements in the
[system architecture](../../spec/architecture/system.md),
[resilience specification](../../spec/resilience.md), and
[observability and security specification](../../spec/observability-security.md).

## Module boundaries

The internal modules under `python/src/agent_framework_mongodb/_shared` are feature-neutral:

- `capabilities.py` represents the result of a deployment or driver capability check.
- `embeddings.py` validates configured dimensions and normalizes generated vectors.
- `field_paths.py` validates configured MongoDB field paths and resolves result fields.

They depend only on public integration errors and standard-library types. Feature modules may use
them, but shared modules must never import Memory, Chat History, RAG, Session Store, or Workflow
Checkpoint types.

## Capability results

`CapabilityResult` is an immutable value containing a capability name, support status, optional
remediation, and optional detected values. Construction enforces these invariants:

- names are non-empty;
- unsupported capabilities always include remediation; and
- detected values are copied into a read-only mapping so caller mutation cannot alter the result.

`require()` returns normally for a supported capability. Otherwise it raises
`MongoDBCapabilityError` with the capability and corrective action. Capability detection itself is
implemented by the feature or provisioning slice that has enough server and mode context.

## Embedding normalization

`validate_dimensions()` rejects booleans, zero, and negative dimensions with
`MongoDBConfigurationError`.

`normalize_embeddings()` receives an already-generated batch and validates it before any MongoDB
operation:

1. Validate the configured dimensions and expected count.
2. Require the generator's vector count to match the input count.
3. Require every vector to have the configured dimensions.
4. Reject booleans and non-real values.
5. Convert accepted values to `float` and reject NaN and infinities.
6. Return immutable tuples for downstream mapping.

Generator invocation is deliberately outside this helper. The Memory and RAG adapters are
responsible for preserving generator exceptions as causes when translating them to
`MongoDBEmbeddingError` and for propagating task cancellation.

## Field-path safety

`validate_field_path()` accepts configured dotted field paths but rejects:

- empty paths or segments;
- null bytes;
- segments beginning with `$`;
- numeric or `$[]` positional array syntax; and
- the internal `_ragScore` alias.

The function returns the original validated path; it does not rewrite names or build MongoDB
expressions. Query builders must still place only validated paths into structured PyMongo
documents.

`resolve_field_path()` walks nested mappings without dynamic evaluation. Missing segments or
non-mapping intermediate values raise `MongoDBMappingError`, keeping stored-data failures distinct
from invalid configuration.

## Public error categories

The package currently exports:

| Error | Boundary |
| --- | --- |
| `MongoDBIntegrationError` | Base category for integration failures |
| `MongoDBConfigurationError` | Invalid caller configuration before I/O |
| `MongoDBEmbeddingError` | Invalid embedding output or translated generator failure |
| `MongoDBCapabilityError` | Unsupported server, deployment, driver, or mode capability |
| `MongoDBMappingError` | Stored or retrieved data cannot be mapped safely |

Later slices extend the taxonomy for index, filter, retrieval, persistence, timeout, and
cancellation behavior. Direct APIs surface these errors; only documented Agent Framework adapter
boundaries may fail open for operational errors.

## Verification

`python/tests/unit/test_validation.py` covers invalid and nested field paths, embedding count,
dimensions, numeric and finite values, actionable capability failures, and immutable detected
values.

Validated commands for this slice:

```powershell
python -m pytest
python -m ruff check src tests
python -m mypy
python -m pyright
python -m build
python -m twine check dist\*
```

The wheel and source distribution were each installed into a new virtual environment, then
`agent_framework_mongodb` was imported successfully. Credentialed MongoDB integration tests are
not part of this foundation slice because none of these helpers contact a server.
