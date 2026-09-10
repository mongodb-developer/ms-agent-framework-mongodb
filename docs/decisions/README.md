# Architectural Decision Records (ADRs)

An Architectural Decision (AD) is a justified software design choice that addresses a functional or non-functional requirement that is architecturally significant. An Architectural Decision Record (ADR) captures a single AD and its rationale.

For more information [see](https://adr.github.io/)

## Decision Index

All decisions remain proposed until their listed deciders approve them in a pull request. The canonical
[implementation specifications](../spec/README.md) define the work that may begin now; proposed ADRs record rationale
but do not override those specifications.

| ADR | Decision | Status |
| --- | --- | --- |
| [0001](0001-use-one-external-cross-language-repository.md) | Use one external cross-language repository | Proposed |
| [0002](0002-separate-memory-history-rag-and-persistence.md) | Separate Memory, Chat History, RAG, and persistence | Proposed |
| [0003](0003-integrate-through-public-agent-framework-contracts.md) | Integrate through public Agent Framework contracts | Proposed |
| [0004](0004-publish-independent-language-packages.md) | Publish independent language packages | Proposed |
| [0005](0005-fix-resource-ownership-at-construction.md) | Fix resource ownership at construction | Proposed |
| [0006](0006-make-index-provisioning-explicit.md) | Make index provisioning explicit | Proposed |
| [0007](0007-use-typed-filters-and-native-search-pipelines.md) | Use typed filters and native search pipelines | Proposed |
| [0008](0008-store-versioned-exact-history-with-atomic-ordering.md) | Store versioned exact history with atomic ordering | Proposed |
| [0009](0009-enforce-behavioral-not-physical-parity.md) | Enforce behavioral rather than physical parity | Proposed |
| [0010](0010-fail-open-only-at-agent-adapter-boundaries.md) | Fail open only at agent adapter boundaries | Proposed |
| [0011](0011-release-features-through-staged-quality-gates.md) | Release features through staged quality gates | Proposed |
| [0012](0012-include-session-and-checkpoint-stores.md) | Include Session Store and Workflow Checkpoint Store | Proposed |
| [0013](0013-establish-project-and-publishing-governance.md) | Establish project and publishing governance | Proposed |
| [0014](0014-publish-only-tested-compatibility-ranges.md) | Publish only tested compatibility ranges | Proposed |
| [0015](0015-default-memory-persistence-to-fail-open.md) | Default Memory persistence to fail open | Proposed |
| [0016](0016-keep-index-facades-in-runtime-packages.md) | Keep explicit index facades in runtime packages | Proposed |
| [0017](0017-use-standard-telemetry-without-unapproved-markers.md) | Use standard telemetry without unapproved markers | Proposed |
| [0018](0018-version-gate-persistence-contracts.md) | Version-gate persistence contracts | Proposed |

External release prerequisites are tracked in the [resolved implementation decisions](../spec/project/scope.md#resolved-implementation-decisions) and Foundation gate.

## How are we using ADRs to track technical decisions?

1. Copy `docs/decisions/adr-template.md` to `docs/decisions/NNNN-title-with-dashes.md`, where NNNN indicates the next number in sequence.
    1. Check existing pull requests to make sure you use the correct sequence number.
    2. Use `docs/decisions/adr-short-template.md` only for a narrow decision with no material alternatives to compare.
2. Edit NNNN-title-with-dashes.md.
    1. Status must initially be `proposed`.
    2. The list of `deciders` must include the GitHub IDs of the people who will sign off on the decision.
    3. The relevant EM and architect must be listed as deciders or informed of all decisions.
    4. You should list the names or github ids of all partners who were consulted as part of the decision.
    5. Keep the list of `deciders` short. You can also list people who were `consulted` or `informed` about the decision.
3. For each option, list the good, neutral, and bad aspects of each considered alternative.
    1. Detailed investigations can be included in the `More Information` section inline or as links to external documents.
4. Share your PR with the deciders and other interested parties.
   1. Deciders must be listed as required reviewers.
   2. The status must be updated to `accepted` once a decision is agreed and the date must also be updated.
   3. Approval of the decision is captured using PR approval.
5. Decisions can be superseded by a new ADR. Record any negative outcomes in the original ADR.
