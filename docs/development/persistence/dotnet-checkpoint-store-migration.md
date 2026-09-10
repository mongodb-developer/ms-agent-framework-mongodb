# .NET Workflow Checkpoint Store: unsupported schema version migration

`MongoDBCheckpointStore` refuses to read, save, or delete a stored checkpoint
document whose `schema_version` marker does not exactly match the constant
this build understands (`MongoDBCheckpointStore.SchemaVersion`). This is
intentional: silently reinterpreting, coercing, or partially mutating a
checkpoint document in an unknown shape risks losing resumable workflow
state. **There is no automated migration.** This document is the exact,
actionable remediation referenced by every exception message the store
raises for this condition.

## Why this happens

- `schema_version` changes when this package changes the BSON checkpoint
  envelope shape (added/removed/retyped envelope fields such as `checkpoint`,
  `sequence`, `parent_checkpoint_id`, or the canonical scope fields).
- A document was written by an older or newer version of this package than
  the one currently loaded, or was migrated/copied from a different
  deployment without also migrating its envelope.
- A document was written against a different resolved
  `Microsoft.Agents.AI.Workflows` version whose checkpoint JSON shape this
  package's `checkpoint` payload bytes are not expected to be reinterpreted
  against (the payload itself is opaque to this store and is never
  validated against a framework schema; only the surrounding envelope's
  `schema_version` is checked).

## How to tell which case applies

The exception message states the expected `schema_version` for this build.
Read the stored document directly to see its actual value, for example from
the `mongosh` shell:

```javascript
db.<collection>.findOne({ _id: "<the failing document's _id>" });
```

## Manual remediation

There is no in-place, automated conversion between schema versions. Choose
one of the following, performed manually and deliberately:

1. **Export, downgrade-read, re-upgrade-write (preferred when the checkpoint
   history must be preserved):**
   1. Export the scoped document(s) exactly as stored (for example
      `mongoexport --collection <collection> --query '{"session_id":"<id>"}'`
      or an equivalent driver read), and keep this raw export until the
      migration is verified.
   2. In an isolated environment, reference the **prior**
      `MongoDB.AgentFramework` package version whose `SchemaVersion` constant
      matches the exported document's `schema_version`, and use its
      `MongoDBCheckpointStore.LoadCheckpointAsync`/`ListCheckpointsAsync` (or
      an equivalent direct read of the `checkpoint` payload bytes) to obtain
      the checkpoint payloads and their lineage (`parent_checkpoint_id`,
      `sequence`).
   3. Using the **currently supported** `MongoDB.AgentFramework` package
      version, call `SaveCheckpointAsync` for each checkpoint **in ascending
      original `sequence` order** (so lineage and sequence allocation are
      re-established consistently) against either a **new collection** or
      the same collection **after removing the old-schema documents**, so
      the currently supported version's `EnsureIndexesAsync`/read/write paths
      are never asked to interpret the old envelope shape.
   4. Verify the new documents' `schema_version` matches the currently
      supported constant, then delete the temporary export.
2. **Delete and recreate (when the checkpoint history does not need to be
   preserved):** delete the incompatible documents directly (for example
   `db.<collection>.deleteMany({ session_id: "<id>", schema_version: { $ne: <current> } })`,
   matched only after independently verifying the authorization scope), and
   let the workflow start a fresh checkpoint history under the currently
   supported schema. This necessarily loses the ability to resume from any
   deleted checkpoint.

Both paths are manual and operator-driven. Do not write code that
automatically reinterprets an unknown `schema_version` -- that is exactly the
lossy, silent-migration behavior this store is designed to refuse.

## Preventing this

- Pin an exact, tested `MongoDB.AgentFramework` package version per
  deployment; do not mix package versions writing checkpoints to the same
  collection.
- Before upgrading the package version in a deployment that already has
  stored checkpoints, read
  [dotnet-checkpoint-store.md](dotnet-checkpoint-store.md) and this
  document's "Why this happens" section to confirm whether the new version
  changed `SchemaVersion`, and plan a maintenance window for the manual
  remediation above if so.
