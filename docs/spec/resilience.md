# Resilience and Error Handling

## Error handling

Define and test these categories:

- **Argument/configuration errors**: invalid dimensions, empty names, invalid limits, incompatible mode options, empty
  memory scope, missing field mappings. Throw immediately.
- **Index errors**: missing index, wrong vector path, wrong dimensions, non-queryable index, unsupported filter field,
  unavailable hybrid capability. Return actionable errors naming the index and required correction.
- **Public search errors**: surface driver and index failures to the caller.
- **Agent hook retrieval errors**: log through the framework logging abstraction and return no extra context when the
  configured resilience policy permits it. Dedicated adapters must propagate cancellation; composed framework
  adapters must document and test the framework's actual cancellation behavior.
- **Memory/History storage errors**: log without corrupting session state; provide an option or public method for callers that
  require fail-fast persistence.

Do not catch `OperationCanceledException`/cancellation equivalents as ordinary operational failures.

### Exception taxonomy

Both packages MUST expose stable integration-level error categories while preserving the driver exception as the
cause/inner exception. Exact names follow language conventions:

| Category | Example causes | Retryable by provider |
| --- | --- | --- |
| Configuration | Empty names, invalid limits, incompatible options | No |
| Embedding | Wrong count/dimensions, generator failure | Only delegated generator policy |
| Capability | Unsupported mode/server/deployment/driver | No |
| Index missing | Required named index absent | No |
| Index mismatch | Wrong path/dimensions/similarity/filter fields | No |
| Index not ready | Building or non-queryable | Only explicit readiness polling |
| Filter translation | Unsupported operator/path for active mode | No |
| Mapping | Required result field absent or wrong type | No |
| Retrieval | MongoDB aggregate/network/command failure | Limited transient policy |
| Persistence | Memory/history/session/checkpoint write failure | Limited transient policy |
| Timeout | Provider deadline exceeded | No additional retry after deadline |
| Cancellation | Caller/framework cancellation | Never |

Public direct operations MUST throw these errors. Agent hooks MAY convert retrieval/persistence operational errors to
empty context or logged persistence failure according to provider options. They MUST NOT suppress configuration,
capability, index-definition, cancellation, or programmer errors.

### Retry and timeout policy

- Rely on official driver retry behavior before adding provider retries.
- Provider retries MAY cover only documented transient network/server-selection conditions and MUST be bounded by an
  overall deadline.
- Aggregate queries MUST NOT be retried after partial result consumption.
- Memory insert retry behavior MUST account for stable document IDs so a retry cannot create duplicate memories.
- Index readiness polling is repeated observation, not a command retry.
- The provider MUST NOT retry an unsupported stage, malformed pipeline, authentication failure, authorization failure,
  missing index, or definition mismatch.
- Public options SHOULD support retrieval, persistence, and index-polling timeouts independently.
- Driver timeouts and cancellation tokens/task cancellation MUST be wired so cancellation can interrupt embedding,
  server selection, aggregate execution, cursor iteration, inserts, and polling delays.

### Fail-open policy

Fail-open applies only at the Agent Framework adapter boundary. The default SHOULD match framework conventions:

- RAG/Memory retrieval operational failure: log a redacted warning and provide no extra context.
- Memory persistence operational failure: log a redacted warning after preserving the agent response.
- Chat History persistence follows the framework history provider's configured failure policy and MUST preserve
  idempotency on retry.
- Direct `search`, `store`, `validate`, and `ensure` methods: always fail to the caller.
- Cancellation: dedicated adapters always propagate; framework-composed adapters MUST test, document, and if
  necessary replace composition when the framework catches cancellation.
- Invalid configuration, unsupported capabilities, and unsafe filters: always fail before model invocation.

An application MAY configure Memory persistence as fail-fast when durable memory is part of its business transaction.
That option and its effect on the returned agent response MUST be documented explicitly.
