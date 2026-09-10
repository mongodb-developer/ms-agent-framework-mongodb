# .NET Session Store: unsupported schema/framework version migration

`MongoDBAgentSessionStore` refuses to load, update, or delete a stored session
document whose `schema_version` or `framework_version` marker does not exactly
match the constants this build understands
(`MongoDBAgentSessionStore.SchemaVersion` /
`MongoDBAgentSessionStore.FrameworkSerializationVersion`). This is
intentional: silently reinterpreting, coercing, or partially mutating a
document in an unknown shape risks data loss. **There is no automated
migration.** This document is the exact, actionable remediation referenced by
every exception message the store raises for this condition.

## Why this happens

- `schema_version` changes when this package changes the BSON envelope shape
  (added/removed/retyped envelope fields such as `session`, `version`,
  `expires_at`, or the canonical scope fields).
- `framework_version` changes when the internal Agent Framework JSON
  serialization compatibility marker this package writes changes -- for
  example, if a future `Microsoft.Agents.AI.Abstractions` version changes how
  `AIAgent.SerializeSessionAsync` shapes `AgentSession` JSON in a way this
  package must track explicitly.
- A document was written by an older or newer version of this package than
  the one currently loaded, or was migrated/copied from a different
  deployment without also migrating its envelope.

## How to tell which case applies

The exception message states which marker(s) mismatched and the exact
supported values for this build (`MongoDBAgentSessionStore.SchemaVersion` and
`MongoDBAgentSessionStore.FrameworkSerializationVersion`). Read the stored
document directly to see its actual values, for example from the `mongosh`
shell:

```javascript
db.<collection>.findOne({ _id: "<the failing document's _id>" });
```

## Manual remediation

There is no in-place, automated conversion between schema/framework
versions. Choose one of the following, performed manually and deliberately:

1. **Export, downgrade-read, re-upgrade-write (preferred when the session
   must be preserved):**
   1. Export the scoped document exactly as stored (for example
      `mongoexport --collection <collection> --query '{"_id":"<id>"}'` or an
      equivalent driver read), and keep this raw export until the migration
      is verified.
   2. In an isolated environment, reference the **prior** `MongoDB.AgentFramework`
      package version whose `SchemaVersion`/`FrameworkSerializationVersion`
      constants match the exported document's markers, and use its
      `MongoDBAgentSessionStore.GetAsync` (or an equivalent direct read of the
      `session` payload bytes) with the *same originating `AIAgent` type* to
      obtain the deserialized `AgentSession`.
   3. Using the **currently supported** `MongoDB.AgentFramework` package
      version, call `CreateAsync` (or `SetAsync` with no `expectedVersion`, to
      replace) against either a **new collection** or the same collection
      **after removing the old-schema document**, so the currently supported
      version's `EnsureIndexesAsync`/read/write paths are never asked to
      interpret the old envelope shape.
   4. Verify the new document's `schema_version`/`framework_version` match the
      currently supported constants, then delete the temporary export.
2. **Delete and recreate (when the session's prior state does not need to be
   preserved):** delete the incompatible document directly (for example
   `db.<collection>.deleteOne({ _id: "<id>" })`, matched only on `_id` plus
   the authorization scope fields you have independently verified), and let
   the application call `CreateAsync` again to establish a fresh session
   under the currently supported schema.

Both paths are manual and operator-driven. Do not write code that
automatically reinterprets an unknown `schema_version`/`framework_version`
combination -- that is exactly the lossy, silent-migration behavior this
store is designed to refuse.

## Preventing this

- Pin an exact, tested `MongoDB.AgentFramework` package version per
  deployment; do not mix package versions writing to the same collection.
- Before upgrading the package version in a deployment that already has
  stored sessions, read
  [dotnet-session-store.md](dotnet-session-store.md) and this document's
  "Why this happens" section to confirm whether the new version changed
  `SchemaVersion` or `FrameworkSerializationVersion`, and plan a maintenance
  window for the manual remediation above if so.
