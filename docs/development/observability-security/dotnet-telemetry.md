# .NET observability telemetry

This document describes the .NET portion of implementation-map
[slice 19](../../spec/implementation-map.md), governed by the
[observability, privacy, and security specification](../../spec/observability-security.md), the
[resilience specification](../../spec/resilience.md), and ADR rationale
[0017](../../decisions/0017-use-standard-telemetry-without-unapproved-markers.md) (standard telemetry only, no
proprietary markers or exporter) and
[0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md) (typed, bounded filters, which the
telemetry contract also never bypasses by logging their contents). The ADR remains proposed and does not override
the specification.

## Why a shared engine

Every provider/store's meaningful public operation needed the same three things -- one activity, one duration
measurement, one structured completion log -- with the same authorized field set. Rather than repeat that logic
(and its redaction guarantees) five times, `Internal.Observability.MongoDBTelemetry.TrackAsync` is the single call
site every instrumented operation goes through. This also makes it structurally impossible for a new operation to
accidentally add an unauthorized field: the helper's signature only accepts the closed vocabulary types, never an
arbitrary tag dictionary.

## Public conventions used

- **`System.Diagnostics.ActivitySource`** named `MongoDB.AgentFramework` (`MongoDBTelemetry.ActivitySourceName`).
  Every operation starts one `Activity` named `mongodb.{feature}.{operation}` (for example
  `mongodb.rag.retrieve`), tagged with `feature`/`operation`/`mode` (when applicable).
- **`System.Diagnostics.Metrics.Meter`** sharing the same name, exposing one histogram instrument,
  `mongodb.agentframework.operation.duration` (milliseconds), tagged with `feature`/`operation`/`mode`/`outcome`/
  `error_category`.
- **`Microsoft.Extensions.Logging.ILogger`**, one structured `Information` (or `Warning` on `Failed`) completion
  log per operation with the same field set plus `duration_ms`.

No exporter, OTLP pipeline, or telemetry backend is referenced anywhere in this project: a consuming application
wires `ActivitySource`/`Meter` names above into whatever OpenTelemetry (or other) pipeline it already runs.

## Telemetry contract fields

Exactly the fields authorized by [observability-security.md](../../spec/observability-security.md)'s telemetry
contract table, each a closed, stable vocabulary (`Internal.Observability.MongoDBTelemetryVocabulary.cs`):

| Field | Values | Notes |
| --- | --- | --- |
| `feature` | `memory`, `history`, `rag`, `session_store`, `checkpoint_store` | One value per public module. |
| `operation` | `retrieve`, `persist`, `delete`, `validate_index`, `ensure_index`, `load`, `list` | Every instrumented method maps onto one of these, never a bespoke per-method name. |
| `mode` | `ann`, `enn`, `full_text`, `hybrid_rrf` | Omitted (no tag/field at all) for operations with no retrieval-mode concept. |
| `outcome` | `success`, `empty`, `failed`, `cancelled` | `cancelled` is recorded by catching `OperationCanceledException` *before* the generic exception handler, so it can never be misclassified as `failed`. |
| `result_count` | integer | Only present when the operation has a countable result (omitted for `ensure_index`, since a returned index name must never be counted or logged -- see below). |
| `candidate_bucket` | `0`, `1-10`, `11-100`, `101-1000`, `1000+` | `Internal.Observability.MongoDBCandidateBucket.Bucket` -- a raw unrestricted candidate/topK count is never recorded, only its bucket. |
| `error_category` | `configuration`, `embedding`, `capability`, `index_missing`, `index_mismatch`, `index_not_ready`, `index_failed`, `index_already_exists`, `index_privilege`, `index_other`, `mapping`, `retrieval`, `persistence`, `timeout`, `concurrency`, `unknown` | `Internal.Observability.MongoDBErrorCategory.Classify` switches on the caught exception's **type only**; the exception's `Message` is never read by the classifier and never reaches a tag, metric dimension, or log field. |

Never recorded, anywhere in this pipeline, matching the specification's exclusion list: database/collection/host
names, query text, field/filter values, document IDs, tenant/user/session identifiers, source URLs, raw BSON,
embeddings, message/memory content, and index names (see the `ensure_index` exception below).

### Index names are a deliberate omission, not an oversight

The specification allows index names in telemetry "only after redaction review." No such review is recorded, so
every instrumented operation -- `EnsureIndexesAsync`, `ValidateIndexesAsync`, and the RAG/Memory index-management
facade operations -- always records `outcome`/(optionally)`result_count` without ever including the created,
validated, or dropped index's name, even though the underlying method returns or accepts one. `classifySuccess`
for these operations ignores its input value entirely and returns a constant `(Success, null, null)`, which is
covered by dedicated tests (`*TelemetryTests.EnsureIndexes*_RecordsEnsureIndexOperationAndOmitsIndexName`) that
assert no tag/log field is ever named `index_name` or matches the configured index name string.

## Instrumentation pattern per operation

Each instrumented method follows the same shape: the original body is renamed `<Name>InnerAsync`, and a thin
public wrapper with the original signature calls:

```csharp
return await MongoDBTelemetry.TrackAsync(
    _logger,
    MongoDBTelemetryFeature.Rag,
    MongoDBTelemetryOperation.Retrieve,
    mode: MongoDBTelemetryMode.Ann,
    () => SearchInnerAsync(query, cancellationToken),
    classifySuccess: results => results.Count > 0
        ? new(MongoDBTelemetryOutcome.Success, results.Count, MongoDBCandidateBucket.Bucket(numCandidates))
        : new(MongoDBTelemetryOutcome.Empty, 0, MongoDBCandidateBucket.Bucket(numCandidates)),
    cancellationToken);
```

`classifySuccess` runs inside `TrackAsync`'s own `try` block and must never throw; it only reads the already
computed result, never re-executes any MongoDB call.

### Avoiding duplicate spans where an adapter calls the direct provider

Several operations are reachable through two public entry points that share the same underlying MongoDB call --
for example `MongoDBCheckpointStore.CreateCheckpointAsync` (the framework-required `JsonCheckpointStore` override
hook) and `SaveCheckpointAsync` (the direct public facade) both delegate to a single private
`SaveCheckpointCoreAsync`. Instrumenting each entry point independently would double-count a single MongoDB round
trip as two activities/log lines. Instead, instrumentation is placed at the shared core method exactly once, so
either caller produces exactly one activity, one metric point, and one log line. This was verified for every
feature that has such a shared boundary (Memory, History, RAG, Session Store, and Checkpoint Store); see the
per-feature developer docs' own telemetry sections for the exact call graph. Where two entry points do **not**
share code (for example `MongoDBCheckpointStore.RetrieveIndexAsync`, the framework override, versus
`ListCheckpointsAsync`, the direct facade -- two independently implemented code paths with no MongoDB call in
common), each is instrumented separately, since there is no duplication risk.

## Cancellation is always distinct from failure

`TrackAsync` catches `OperationCanceledException` in its own `catch` clause, ahead of the generic
`catch (Exception)` clause, and records `outcome = cancelled` with no `error_category` at all (not `unknown`,
not any other category -- the field is omitted). This makes cancellation observably different from every other
failure mode in metrics, activities, and logs, matching
[resilience.md](../../spec/resilience.md)'s "Do not catch `OperationCanceledException`... as ordinary operational
failures." Every `*TelemetryTests.cs` file includes a `WhenCanceled_RecordsCancelledOutcomeDistinctFromFailed`
(or equivalently named) test asserting `outcome == cancelled` and `error_category == null`.

## Redaction under adapter fail-open logging

Per [resilience.md](../../spec/resilience.md)'s fail-open policy (ADR
[0010](../../decisions/0010-fail-open-only-at-agent-adapter-boundaries.md)), Agent Framework adapter boundaries
(the `ContextProvider`/`HistoryProvider` hooks) may swallow an operational failure and log a redacted warning
instead of throwing. Because those adapters call through the same instrumented direct methods described above,
they get the same `TrackAsync` failure handling for free: the exception's message never reaches the log (only its
type-derived `error_category` does), so a fail-open adapter's own additional logging around the swallowed
exception must likewise never format the exception's `Message`/`ToString()` into a log argument. Every provider's
adapter fail-open path was audited for this and is covered by a sentinel-secret test (see below).

## Sentinel-secret redaction tests

Each `*TelemetryTests.cs` file (`dotnet/tests/MongoDB.AgentFramework.Tests/Observability/`) includes at least one
test that:

1. Injects a fake driver exception whose `Message` contains a high-entropy sentinel string
   (`SENTINEL-SECRET-...`), via each feature's own test double (for example `RAGCollectionState.AggregateException`,
   `SessionCollectionState.InsertException`, a hand-built `MongoCommandException` with the sentinel embedded in
   `errmsg` for stores whose write path wraps driver exceptions).
2. Invokes the instrumented public operation and asserts the expected wrapped/unwrapped exception type is thrown
   (matching each store's own exception-translation behavior).
3. Asserts every `Activity.TagObjects` value and every recorded log `state` value does **not** contain the
   sentinel string (`Assert.DoesNotContain(SentinelSecret, ...)`), across the single captured activity/log entry.

This proves the redaction guarantee empirically rather than only by code inspection: if any future change ever
threaded the raw exception message into a tag or log argument, these tests would fail immediately.

## Overhead when disabled

`ActivitySource.StartActivity` returns `null` (a fully inert `Activity?`) when no `ActivityListener` is subscribed,
and `Meter`/`Histogram<T>.Record` are no-ops when no `MeterListener` is enabled -- both by the .NET runtime's own
design, not anything this project implements. `TrackAsync` additionally checks `logger.IsEnabled(level)` before
building the structured log `state` list, so when logging is below the configured minimum level no allocation or
message formatting occurs either. With every listener disabled, the only unavoidable cost is the `classifySuccess`
delegate invocation (already computing a value the caller needed anyway) and a `Stopwatch.GetTimestamp()`/
`GetElapsedTime()` pair.

## Test isolation for `ActivityListener`/`MeterListener`

`ActivityListener` and `MeterListener` are process-wide, and xunit runs test classes in parallel by default, so a
naive listener registered in one test class can observe activities started by a concurrently running,
unrelated test class. `dotnet/tests/MongoDB.AgentFramework.Tests/Observability/ObservabilityTestSupport.cs`
provides `TelemetryTestScope` (starts a root `Activity` via the legacy constructor, establishing a distinct
`RootId` for the current async flow) and `ActivityCapture.StoppedUnder(scope)` (filters captured activities down
to only those sharing that `RootId`). Every telemetry test in this repository uses both; omitting either
reintroduces cross-test flakiness.
