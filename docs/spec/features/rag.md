# Retrieval-Augmented Generation (RAG)

Index lifecycle and provisioning requirements shared by Memory and RAG are specified in [Index Management](index-management.md).

## RAG feature requirements

### Prototype source

The current `feature/mongodb-memory` branch contains a validated prototype:

- Python package: `python/packages/mongodb`
- .NET package: `dotnet/src/Microsoft.Agents.AI.MongoDB`
- Python unit and Atlas-gated integration tests
- .NET unit tests and sample
- CI wiring for Python integration tests

Extract behavior and tests from that branch rather than reimplementing from memory. Preserve authored Git history when
practical, but do not transfer unrelated files, generated outputs, or local `.gitignore` changes.

### Purpose

The RAG provider performs read-only retrieval from an existing knowledge collection and supplies relevant chunks to
the model. It does not write conversation messages, ingest source documents during agent invocation, or mutate the
knowledge collection.

Vector and hybrid retrieval MUST use a caller-provided embedding generator. Server-side automated embeddings are not
part of the provider contract. The query-time embedding model and dimensions MUST be compatible with the vectors
already stored in the configured knowledge collection.

MongoDB RAG is analogous to Neo4j GraphRAG in its Agent Framework role, but it is not a graph provider. MongoDB-specific
enrichment may use controlled aggregation stages such as `$lookup` after retrieval.

### Search modes

Support these modes as independently testable capabilities:

1. **Vector**

   - Embed the query.
   - Use `$vectorSearch` against a configured vector index and vector field.
   - Support approximate nearest-neighbor search and exact search when supported.
   - Apply configured prefilters in `$vectorSearch`.
   - Return MongoDB's vector search score.

2. **Full-text**

    - Use MongoDB Search `$search` against a configured Search index and text field(s).
    - Return MongoDB's search score.
    - Support a documented, bounded set of search options rather than exposing every MongoDB Search operator.

3. **Hybrid**

   - Combine vector and full-text result sets using an officially supported MongoDB rank-fusion mechanism.
   - Keep vector and full-text index names independently configurable.
   - Normalize or fuse scores according to MongoDB's documented semantics; do not compare raw scores directly.
   - Detect server/deployment capability and fail clearly when hybrid search is unavailable.

Vector ANN, Vector ENN, full-text, and hybrid RRF are all required RAG capabilities. Each mode has an independent
implementation gate and MUST pass its capability, authorization, and real-deployment tests before Release 1.0.

### Retrieval invocation modes

RAG MUST support two Agent Framework integration modes with the same underlying direct-search implementation:

1. **Before invoke**: construct a query from configured recent messages and automatically inject attributed context.
2. **On-demand tool**: expose a read-only search tool that the model may call when retrieval is needed.

On-demand tool requirements:

- the model supplies query text only
- search mode, collection, indexes, fields, candidate limits, final limits, enrichment, and authorization filters are
  application-owned and immutable for a tool instance
- the tool schema MUST NOT expose raw BSON, a filter document, a field name, an operator, or an aggregation pipeline
- tool name and description are configurable and validated
- applications MAY require Agent Framework tool approval for sensitive collections
- direct search remains available for deterministic workflows and testing
- Python SHOULD install the tool through the public context/tool extension mechanism; .NET MAY use
  `TextSearchProvider` on-demand behavior only if the compatibility tests described above pass

The provider MAY offer both modes as separate instances. One instance MUST NOT perform automatic retrieval and expose
the same retrieval tool simultaneously unless duplicate retrieval behavior is explicitly designed and tested.

### Search-mode option contract

| Option | Vector ANN | Vector ENN | Full text | Hybrid RRF |
| --- | --- | --- | --- | --- |
| Query embedding | Required | Required | Not used | Required |
| Vector index | Required | Required | Not used | Required |
| Search index | Not used | Not used | Required | Required |
| `numCandidates` | Required/defaulted | Forbidden | Not used | Required/defaulted in vector input |
| `exact` | `false`/omitted | `true` | Not used | Initial hybrid vector input uses ANN |
| `topK` | Required | Required | Required | Required final limit |
| Vector filter | Supported | Supported | Not used | Required in vector input when configured |
| Search compound filter | Not used | Not used | Supported | Required in text input when configured |
| Fusion weights | Not used | Not used | Not used | Optional/defaulted |

`numCandidates` and `exact: true` are mutually exclusive. Constructors or effective-query validation MUST reject an
invalid combination before contacting MongoDB. `numCandidates` MUST be greater than or equal to `topK`; the default
SHOULD be documented and bounded to prevent unexpectedly expensive queries.

Hybrid exact-vector behavior is out of scope. Hybrid RRF uses ANN for its vector input.

### Pipeline construction rules

- Pipelines MUST be built with structured driver APIs or BSON documents, never string concatenation.
- .NET MUST use typed `MongoDB.Driver` builders for supported stages and expressions. BSON MAY be used for a stage or
  option not represented by the minimum supported driver.
- User values MUST remain BSON values and MUST NOT be interpolated into field or operator text.
- Configured field paths, index names, result aliases, and enrichment paths MUST pass allowlist validation.
- A model response MUST never supply a MongoDB operator, field path, index name, or pipeline.
- Retrieval stage order is normative. Optimization MUST NOT move security filters after candidate selection or limit.

The pseudocode below is logical BSON. Implementations MUST preserve its semantics while using language-appropriate
driver APIs.

### Vector ANN pipeline

`$vectorSearch` MUST be the first stage. The mandatory filter belongs inside `$vectorSearch.filter`; every referenced
field MUST be configured as a Vector Search index field of type `filter`.

```javascript
[
  {
    $vectorSearch: {
      index: vectorIndex,
      path: vectorField,
      queryVector: queryEmbedding,
      numCandidates: numCandidates,
      limit: topK,
      filter: mandatoryFilter
    }
  },
  { $set: { _ragScore: { $meta: "vectorSearchScore" } } },
  ...approvedPostVectorEnrichment,
  { $project: mappedResultFields }
]
```

The `filter` property SHOULD be omitted when there is no effective filter. `approvedPostVectorEnrichment` MUST NOT
remove or overwrite `_id`, `_ragScore`, configured text/source fields, or mandatory authorization fields before final
mapping.

### Vector ENN pipeline

ENN uses the same Vector Search index. Exact search is a query mode, not an index type.

```javascript
[
  {
    $vectorSearch: {
      index: vectorIndex,
      path: vectorField,
      queryVector: queryEmbedding,
      exact: true,
      limit: topK,
      filter: mandatoryFilter
    }
  },
  { $set: { _ragScore: { $meta: "vectorSearchScore" } } },
  ...approvedPostVectorEnrichment,
  { $project: mappedResultFields }
]
```

The provider MUST NOT emit `numCandidates` in this pipeline.

### Full-text pipeline

`$search` MUST be the first stage. The required surface supports the `text` operator, one or more configured text
paths, and a bounded subset of compound-filter operators. It does not expose arbitrary Search operators.

```javascript
[
  {
    $search: {
      index: searchIndex,
      compound: {
        must: [
          { text: { query: queryText, path: textFields } }
        ],
        filter: translatedMandatoryFilters
      }
    }
  },
  { $limit: topK },
  { $set: { _ragScore: { $meta: "searchScore" } } },
  ...approvedPostSearchEnrichment,
  { $project: mappedResultFields }
]
```

The `filter` array SHOULD be omitted when empty. The provider MUST request detailed Search scoring only when the caller
explicitly opts in and the active deployment supports it. Detailed score payloads are diagnostics and MUST NOT become
a stable public schema.

### Hybrid rank-fusion pipeline

Initial hybrid retrieval MUST use native `$rankFusion`, not application-side score normalization. `$rankFusion` uses
weighted reciprocal-rank fusion, de-duplicates same-collection results, and avoids comparing incomparable vector and
text raw scores.

```javascript
[
  {
    $rankFusion: {
      input: {
        pipelines: {
          vector: [
            {
              $vectorSearch: {
                index: vectorIndex,
                path: vectorField,
                queryVector: queryEmbedding,
                numCandidates: vectorCandidates,
                limit: vectorLimit,
                filter: vectorMandatoryFilter
              }
            }
          ],
          text: [
            {
              $search: {
                index: searchIndex,
                compound: {
                  must: [
                    { text: { query: queryText, path: textFields } }
                  ],
                  filter: searchMandatoryFilters
                }
              }
            },
            { $limit: textCandidates }
          ]
        }
      },
      combination: {
        weights: { vector: vectorWeight, text: textWeight }
      },
      scoreDetails: includeScoreDetails
    }
  },
  { $limit: topK },
  ...approvedPostFusionEnrichment,
  { $project: mappedResultFields }
]
```

Hybrid rules:

- Both input pipelines MUST query the same collection.
- Each input MUST be a legal ranked selection pipeline and MUST leave input documents unmodified.
- Mandatory tenant and authorization constraints MUST be represented independently inside both input retrieval
  stages. A post-fusion filter is not an authorization boundary.
- Projection, `$lookup`, `$unwind`, score aliasing, and other document modification MUST occur after `$rankFusion`.
- Input candidate limits SHOULD exceed final `topK`; defaults and maximums MUST be documented.
- Weight defaults SHOULD be `1.0` for both inputs. Weights MUST be finite and non-negative, and at least one MUST be
  greater than zero.
- `scoreDetails` MAY be returned as raw diagnostic metadata when explicitly requested. Its internal shape is not a
  compatibility guarantee.
- `$scoreFusion` is out of scope and MUST NOT replace `$rankFusion`.

### Filter model and translation

The public filter API MUST be typed or operator-limited. Required operators include:

- equality and inequality
- membership (`in`/`not in`) with bounded value counts
- numeric/date range comparisons
- conjunction and disjunction with bounded nesting depth

Each logical filter MUST have translators for Vector Search and Search. Before execution, the provider MUST either:

1. translate the complete mandatory filter into every retrieval branch, or
2. reject the request with an actionable unsupported-filter error.

Partial translation and application-side authorization filtering are forbidden. A callback that derives filters from
session state returns the same typed filter model; it does not return raw BSON or JSON.

### Score semantics

- Vector results expose MongoDB `vectorSearchScore` as `Score`.
- Full-text results expose MongoDB `searchScore` as `Score`.
- Hybrid results expose the fused rank score as `Score` when available through the supported stage projection.
- Scores are comparable only within results from the same mode and query.
- The provider MUST NOT label scores as probabilities or normalize them into a fabricated universal range.
- Deterministic ordering SHOULD use score descending and a stable document-ID tiebreaker where MongoDB stage
  semantics permit it.

### Capability matrix

The implementation MUST maintain and publish a tested matrix rather than infer support from one Atlas documentation
page.

| Mode | Deployment capability | Server gate | Required indexes | Driver gate |
| --- | --- | --- | --- | --- |
| Vector ANN | Vector Search capable | Current documented minimum | Vector Search | `$vectorSearch` aggregation support |
| Vector ENN | Vector Search with exact query support | Current exact-search minimum | Vector Search | `exact` option support |
| Full text | MongoDB Search capable | Deployment-specific | Search | `$search` aggregation support |
| Hybrid RRF | Search and Vector Search capable | MongoDB 8.0+ | Vector Search + Search | `$rankFusion` support or validated BSON fallback |
| Score fusion | Out of scope | Not applicable | Not applicable | Not implemented |

Capability evaluation MUST consider:

- configured search mode
- deployment type and enabled Search capabilities
- server version
- driver version
- embedding availability and dimensions
- index existence, definition, status, and queryability
- the MongoDB 8.0 enablement/support-case caveat documented for `$rankFusion`, where applicable

Capabilities SHOULD be represented as an internal immutable result with supported/unsupported status, detected values,
and a remediation message. Detection MUST be cacheable for a bounded interval, explicitly refreshable, and testable
without network access. No mode may silently downgrade to another mode.

The term **MongoDB Search/Vector Search index** is preferred throughout the implementation. Use **Atlas Search** only
when a requirement is genuinely Atlas-specific; Search-capable Enterprise or Community deployments must not be
excluded by naming alone.

### Field-path validation

Nested field paths are supported, but configured paths MUST:

- be non-empty dot-delimited field names
- reject segments beginning with `$`
- reject null bytes, empty segments, and positional/update syntax
- reject collision with reserved internal aliases such as `_ragScore`
- be resolved without using `eval`, dynamic code, or string-built pipelines

Missing optional title, URL, or metadata fields produce `null`/empty normalized values. A missing ID or chunk-text
field is a mapping error unless the options explicitly define a fallback. A non-array vector field, wrong dimensions,
or non-numeric vector values are index/data errors, not silently skipped configuration errors.

### RAG field mapping

The provider must work with existing collections rather than impose one storage schema. Require explicit or defaulted
field mappings:

- document identifier
- chunk text
- vector embedding
- source title/name
- source URL
- optional metadata fields
- optional tenant/security filter fields

Nested MongoDB field paths must be supported where the driver permits them.

### RAG result model

Return a normalized result while preserving the raw MongoDB document:

```text
MongoDBRAGResult
  Id
  Text
  SourceName
  SourceUrl
  Score
  Metadata
  RawDocument
```

Python must translate source information into Agent Framework citation annotations when injecting retrieved content.
.NET should map MongoDB results to the framework's `TextSearchProvider.TextSearchResult` behavior, either by composing
`TextSearchProvider` or by matching its citation and context formatting semantics. Composition is preferred when it
avoids duplicate recent-message and on-demand search behavior.

`TextSearchResult` does not expose first-class score or metadata properties. A composed .NET adapter MUST place the
complete `MongoDBRAGResult`, including score, metadata, ID, and raw BSON, in `RawRepresentation`; its context formatter
MAY read that representation. The direct MongoDB search API MUST return `MongoDBRAGResult` and MUST NOT reduce public
results to `TextSearchResult`. A dedicated adapter must preserve the same information through its own result/context
path.

### Query input

- Default to current non-empty user and assistant input messages according to framework conventions.
- Support a configurable recent-message window for follow-up questions.
- Do not embed provider-generated retrieval context.
- Reject an empty public search query.
- Batch only where the MongoDB query mode or embedding interface benefits from batching.

### Filtering and multitenancy

- Accept a static caller-configured filter suitable for tenant or authorization constraints.
- Allow a narrowly scoped callback to derive filters from invocation/session state when needed.
- Document supported filter operators for each search mode.
- Validate that configured vector prefilter fields are indexed as filter fields.
- Treat authorization filters as security controls, not optional post-processing.
- Do not accept arbitrary untrusted JSON or aggregation pipelines from model output.

### Enrichment

An advanced caller may configure approved aggregation stages after retrieval for metadata enrichment, including
`$lookup`, `$unwind`, and `$project` where valid. The provider must retain control over:

- the initial retrieval stage
- query embedding
- mandatory filters
- candidate and result limits
- score capture
- final result mapping

An unrestricted replacement pipeline and custom pipeline callback are out of scope. Adding a typed extension requires
an ADR that defines its invariants, authorization boundary, and security validation.

### Parent-document retrieval

Parent-document RAG is REQUIRED as a documented sample and supported schema pattern, but not as a production ingestion
API. It searches small embedded child chunks and returns bounded, de-duplicated parent documents to the model.

Recommended schema:

```json
{
  "_id": "document identifier",
  "record_type": "parent | child",
  "parent_id": "parent identifier for child records",
  "content": "parent text or child text",
  "embedding": [0.0],
  "tenant_id": "mandatory isolation field",
  "source": { "name": "...", "url": "..." },
  "metadata": {}
}
```

Only child records are embedded and included in the Vector Search path. Retrieval MUST:

1. constrain Vector Search to authorized child records
2. capture each child score and ID
3. resolve configured `parent_id` values through an allowlisted same-database collection or same-collection lookup
4. require the parent to satisfy the same mandatory authorization scope
5. de-duplicate parents while retaining the best matching child score and optional child diagnostics
6. bound child candidates, parents per query, parent text size, lookup fan-out, and final context tokens

Parent hydration MAY use approved post-vector `$lookup`/`$unwind` stages or a bounded second query. The implementation
MUST test both security placement and ordering. Chunking, embedding, and writing parent/child records remain sample
bootstrap responsibilities.

### Structured metadata retrieval

Unrestricted LangChain-style self-query retrieval remains excluded because model-generated filters are not
authorization controls. The repository SHOULD provide an opt-in sample using Agent Framework structured output:

1. Describe an allowlisted metadata schema to a query-planning model.
2. Produce a typed `MetadataQueryPlan` containing semantic text, relevance filters, and an optional requested limit.
3. Validate every field, type, operator, value count, nesting depth, and requested limit.
4. Translate the validated plan into the provider's typed filter AST.
5. Combine relevance filters with application-owned authorization filters using mandatory conjunction.
6. Execute through the normal direct-search path.

Unknown fields/operators, type mismatches, or excessive limits MUST fail closed. The planner MUST never emit BSON or
an aggregation pipeline. The sample MUST display the validated plan for audit and distinguish model-derived relevance
filters from non-negotiable authorization filters.

### Retrieval strategy extensions

The provider returns score order from MongoDB. Optional sample-only post-retrieval strategies are:

- minimum score threshold, with mode-specific semantics
- Maximal Marginal Relevance or other diversification over a bounded candidate set
- metadata-aware or model-based reranking
- duplicate-content suppression
- contextual compression and token-budget trimming

Strategies MUST run after mandatory MongoDB filtering, preserve source attribution, accept bounded inputs, and expose
their effect in diagnostics. They MUST NOT be presented as MongoDB-native capabilities unless MongoDB performs them.
Threshold defaults MUST be mode-specific because vector, Search, and fused scores are not interchangeable.
