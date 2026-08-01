# Observability, Privacy, and Security

## Observability and privacy

- Use standard Python logging and `Microsoft.Extensions.Logging` in .NET.
- Log operation names, durations, result counts, index names, and failure categories.
- Do not log connection strings, credentials, embeddings, raw queries, memory contents, retrieved chunks, or filters
  containing user identifiers at normal log levels.
- If sensitive telemetry is supported, require explicit opt-in and follow Agent Framework redaction conventions.
- Add tracing only through public OpenTelemetry or Agent Framework conventions; do not create a parallel telemetry
  system.
- Record feature usage separately for Memory, Chat History, RAG, Session Store, and Workflow Checkpoint Store if the
  framework exposes a public mechanism appropriate for external integrations.

### Telemetry contract

Operations SHOULD emit one duration metric/span and structured completion log using stable low-cardinality fields:

| Field | Examples |
| --- | --- |
| Feature | `memory`, `history`, `rag`, `session_store`, `checkpoint_store` |
| Operation | `retrieve`, `persist`, `delete`, `validate_index`, `ensure_index`, `load`, `list` |
| Mode | `ann`, `enn`, `full_text`, `hybrid_rrf` |
| Outcome | `success`, `empty`, `failed`, `cancelled` |
| Result count | Integer |
| Candidate bucket | Bounded bucket, not raw unrestricted value |
| Index name | Allowed only after redaction review |
| Error category | Stable taxonomy value, not exception message |

Database and collection names, deployment hostnames, query text, field values, filter values, document IDs, tenant/user
IDs, source URLs, raw BSON, embeddings, and message content MUST NOT be span attributes or normal logs. Exception
messages from drivers may contain server details and MUST pass through the chosen logging/redaction convention.

The project SHOULD reuse Agent Framework/OpenTelemetry activity sources and semantic conventions where public and
applicable. It MUST NOT export telemetry directly or require one telemetry backend.

## Security requirements

- Run dependency and secret scanning in CI.
- Pin or constrain dependencies sufficiently to avoid known vulnerable transitive versions while allowing compatible
  security updates.
- Validate field and index names before inserting them into BSON pipelines.
- Construct pipelines with driver BSON/structured APIs, not string concatenation.
- Never execute model-generated MongoDB queries or pipelines.
- Document least-privilege roles separately for runtime retrieval, memory writes, and index provisioning.
- Ensure integration-test cleanup can delete only test-prefixed resources.
- Require TLS-capable production connection strings and document Atlas network-access requirements.

### Threat model checklist

Before each published release, review at minimum:

- cross-tenant retrieval caused by missing or partially translated filters
- prompt injection inside retrieved content; context MUST remain attributed data, not trusted instructions
- BSON/operator injection through field paths, filters, or enrichment options
- model-generated query execution
- excessive `topK`/candidate values and costly query amplification
- connection-string and driver-error leakage
- unrestricted `$lookup` targets or enrichment stages
- index provisioner credentials used by runtime applications
- integration-test cleanup targeting non-test resources
- dependency and package-supply-chain compromise

Configured enrichment MUST use an allowlist of stage types and validated collection/field names. Cross-database
`$lookup`, `$out`, `$merge`, `$function`, `$accumulator`, JavaScript execution, and write stages are forbidden in the
initial provider.
