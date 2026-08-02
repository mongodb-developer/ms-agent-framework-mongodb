# Python explicit index management

This document describes implementation-map [slice 13](../../spec/implementation-map.md)
for Python. The normative requirements are [Index Management](../../spec/features/index-management.md),
[Memory](../../spec/features/memory.md), [RAG](../../spec/features/rag.md), and the
resilience, security, testing, and sample specifications. ADRs
[0006](../../decisions/0006-make-index-provisioning-explicit.md) and
[0016](../../decisions/0016-keep-index-facades-in-runtime-packages.md) explain why
provisioning is explicit. They remain proposed and do not override the specifications.

## Architecture and public seams

`MongoDBMemoryContextProvider` and `MongoDBRAGContextProvider` are the public
feature-specific facades. They delegate to the shared managers in
`python/src/agent_framework_mongodb/_shared/indexes.py`; runtime retrieval and Agent
Framework hooks never call a mutating manager operation.

Public operations cover list, named inspection, read-only validation, explicit ensure,
create, update, bounded readiness waiting, and drop. RAG exposes independent Vector
Search and Search methods. Memory exposes Vector Search separately from regular
compound and optional TTL methods. `ensure_*` is a deployment action: it creates a
missing definition or updates a mismatched definition, but it never retries a server
`FAILED` state.

The immutable result contracts are exported from `agent_framework_mongodb`:

- `MongoDBIndexResult`
- `MongoDBIndexState`
- `MongoDBVectorIndexDefinition`
- `MongoDBSearchIndexDefinition`
- `MongoDBRegularIndexDefinition`

`MongoDBIndexState` distinguishes `MISSING`, `BUILDING`, `READY`,
`READY_NOT_QUERYABLE`, `FAILED`, and `TIMEOUT`. A successful create/update command is
reported as building (or its inspected non-ready state), never as ready. A ready result
requires both server status `READY` and `queryable == true`.

## Definitions and equivalence

Vector validation compares name, `vectorSearch` type, vector path, dimensions,
similarity, and every required filter path. Additional server fields and filter paths
are tolerated. Search validation compares name, Search type, dynamic mapping mode,
every configured text path, index and search analyzer, and every typed filter mapping.
Nested mappings and multi-mappings are traversed structurally. BSON object key order
and server-added defaults do not affect equivalence.

Memory's `memory_scope_admin` compound key order is significant. When retention is
configured, `memory_expiration_ttl` must index only `expires_at` with
`expireAfterSeconds: 0`. Search/Vector Search indexes and regular indexes use separate
driver APIs and cannot provision each other. An explicitly configured collation is
validated; an unconfigured server default is tolerated.

## Polling, errors, and cancellation

Readiness polling computes one `time.monotonic()` deadline, fetches only the configured
name, and sleeps for at most the lesser of the interval and remaining time. Python task
cancellation propagates through every list/create/update/drop request and every delay.
Timeout and non-queryable errors name the index, last state, and remediation. Driver
exceptions remain causes of stable authorization, capability, transient, missing, or
retrieval error categories. Diagnostics do not include command documents, connection
strings, definitions returned by the server, embeddings, or filters.

## Privileges

Use separate identities:

| Identity | Least-privilege operation categories |
| --- | --- |
| Memory runtime | Read, aggregate, and insert on the memory collection; execute Search queries |
| RAG runtime | Read and aggregate on the knowledge collection; execute Search queries |
| Index provisioner | List, create, update, and drop Search indexes only on approved collections; create/drop approved regular Memory indexes |
| Integration tests | Create/drop uniquely prefixed test collections and indexes in an isolated database |

Do not grant index-management permissions to runtime identities. Exact built-in or
custom roles vary by MongoDB deployment and must be verified against that deployment's
current documentation before release.

## Provisioning example

`python/samples/index_provisioning.py` is an explicit deployment sample. It reads
`MONGODB_URI` and `MONGODB_DATABASE`, uses application-owned collection/index names,
waits with a bounded deadline, and prints only names and states. It does not ingest
documents and is not called by runtime code.

## Verification

Public-facade unit coverage is in
`python/tests/unit/test_index_management.py`, with collection system-boundary fakes.
Credential-gated real-deployment coverage is in
`python/tests/integration_indexing/test_index_management_integration.py`; resources
have unique `af_index_test_` prefixes and cleanup drops only the created collection.

Validated commands for this slice are recorded in the implementing commits. The real
deployment test skips unless `MONGODB_URI` and `MONGODB_DATABASE` are present.
