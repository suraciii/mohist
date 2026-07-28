## Context

`WorkflowRunMetadata` currently serializes only name, creation time, labels, and annotations. Workflow identity is instead encoded as `projectId`, `issueNumber`, and `epicNumber` strings in annotations; `WorkflowRunLineage`, `WorkflowGrain`, and SQLite computed projections parse those strings. `WorkflowRuns.EpicNumber` is already a persisted read-side snapshot used to refresh the JSON-backed Run after reload.

The change affects the Workflow bounded context only. It must preserve the existing Issue-context semantics, CloudEvent extension names and values, API read shape, and project/issue/epic query indexes. The single control-plane daemon applies EF migrations through `DatabaseInitializer` before serving requests.

## Goals / Non-Goals

**Goals:**

- Make Project ID, Issue number, and optional Epic number typed `WorkflowRunMetadata` fields.
- Make typed context the sole source for ownership, profile selection, context refresh, event lineage, and JSON-backed read projections.
- Remove system lineage keys from annotations while preserving unrelated user annotations.
- Upgrade historical `WorkflowRuns.State` atomically with its SQLite projections and retain the current persisted Epic snapshot.
- Preserve Orleans serialization compatibility by only appending field IDs.

**Non-Goals:**

- Change Issue, Epic, WorkflowRun, CloudEvent, or API semantics.
- Change annotations into a validated or schema-governed feature.
- Change lineage representation in other aggregates or add independent WorkflowRun columns for Project and Issue.
- Support mixed-version Server processes during the state migration.

## Decisions

### Append nullable typed metadata fields

Add `ProjectId`, `IssueNumber`, and `EpicNumber` to `WorkflowRunMetadata` with new Orleans field IDs after the existing `0..3` fields. Keep the fields nullable in the serialized record so historical payloads deserialize; enforce non-empty Project and positive Issue at Issue-backed creation and ownership boundaries. `EpicNumber` remains nullable by domain meaning.

This retains old serialized state compatibility and represents optional generic-run context without sentinels. Replacing or reordering existing IDs would break stored Orleans payloads. Using zero or an empty string as an absence sentinel would reintroduce parse-like invalid states.

### Centralize typed lineage operations

Refactor `WorkflowRunLineage` to construct and compare typed context, update only the Epic field during a validated Issue-context refresh, derive `EpicAffiliationOf` directly from the field, and build CloudEvent extensions from typed values. Remove annotation-based lineage construction and parsing, including `RequiredAnnotation` and the silent parse-return path in `RestoreStoredEpicNumber`.

`WorkflowGrain` reads typed Project and Issue values for ownership checks and startup profile resolution. `TaskLogService.ResolvePublishScopeAsync` reads the typed Project ID when it maps a workflow task log to its project-scoped notification audience. The existing row-level `EpicNumber` remains the persisted refresh source: after deserialization, it overwrites the metadata Epic field when present, then normal saving keeps both representations aligned.

Keeping conversion logic at each caller was rejected because it would distribute field/annotation precedence and validation rules across grain, store, and event code. Removing the row-level Epic snapshot was rejected because existing indexed joins and durable membership refresh use it.

### Migrate JSON and computed projections in one EF migration

Create an EF migration that rebuilds SQLite's `WorkflowRuns` table, preserving all ordinary columns, foreign keys, concurrency state, and indexes while changing `MetadataProjectId` and `IssueNumber` computed expressions to the new metadata JSON paths. In the same migration, update each valid legacy state document: copy legacy Project and Issue annotations to typed metadata fields, set typed Epic from `WorkflowRuns.EpicNumber` when it is non-null (otherwise from the legacy annotation), and remove all three system keys from annotations.

The migration must recognize the persisted JSON casing already supported by current projections. It must preserve unrelated annotation entries and recreate every existing index, including Project/Issue, Project/Epic, scheduling, and profile-binding indexes. `Down` performs the inverse JSON transformation and restores legacy computed expressions so an operator can explicitly downgrade the database before returning to the prior Server binary.

Lazy read-time conversion was rejected because untouched historical rows would retain system annotations and stale computed projections indefinitely. Adding physical Project/Issue columns was rejected because the aggregate state remains the authority and computed projections already serve indexed reads.

### Treat migration as an all-or-nothing Server upgrade

Deploy by stopping the Server, applying the EF migration through normal startup, then starting the new binary. The database migration and state rewrite complete before the Server accepts work, so no active grain can save annotation-backed state after migration. A failed migration prevents startup and leaves the transaction rolled back.

Rolling deployment was rejected because old binaries require lineage annotations while new binaries remove them. The documented single control-plane daemon makes a stop-migrate-start rollout sufficient.

### Verify behavior at domain, storage, and schema boundaries

Add focused tests for typed metadata serialization, Issue-backed creation, ownership rejection, Epic refresh including terminal-run behavior, user annotation preservation, unchanged CloudEvent extensions, and workflow task-log project routing. Add migration specs that begin with annotation-backed rows, run EF migrations, and assert transformed JSON, computed Project/Issue values, current `EpicNumber` precedence, and preserved indexes. Update `design/conventions.md` with a temporary implementation-gap note before code changes and remove it when the migration and model ship.

## Risks / Trade-offs

- [SQLite generated columns require a table rebuild, risking omitted schema details] -> Recreate the table from the current migration model, explicitly preserve every column/index/foreign key, and assert the resulting schema in migration specs.
- [Historical JSON has casing variants or malformed lineage values] -> Convert all existing supported casing variants; migrate only valid legacy context and fail verification on fixture cases that cannot preserve an Issue-backed Run's required context rather than silently dropping it.
- [The row Epic snapshot and JSON context can disagree] -> Treat non-null `WorkflowRuns.EpicNumber` as the current durable affiliation during migration and reload, then persist the aligned typed metadata on the next save.
- [An old Server binary writes annotation-backed state after upgrade] -> Stop the single Server before migration and do not run mixed-version instances.
- [A rollback reintroduces system annotation keys] -> Limit this to an explicit operational downgrade; the forward deployment permanently reserves annotations for users.

## Migration Plan

1. Add the temporary `design/conventions.md` implementation-gap note.
2. Add appended Orleans metadata fields and refactor lineage and grain callers to use them.
3. Generate an EF migration that rebuilds `WorkflowRuns`, converts legacy state documents, changes computed JSON paths, and recreates the existing indexes and profile-binding foreign key.
4. Add domain, storage, and schema migration coverage using in-memory SQLite migration fixtures; run the Server test suite and Web typecheck.
5. Stop the Server and deploy the new binary. `DatabaseInitializer` applies the migration before serving requests; verify no persisted state retains the system annotation keys and that projected Project/Issue values remain queryable.
6. On a failed deployment, stop the new binary, run the migration downgrade to restore legacy JSON/projections, and restart the prior binary. If the database migration transaction fails, do not start the new binary.
7. Remove the temporary implementation-gap note after the target representation is deployed.

## Open Questions

None. Field optionality, Epic precedence, migration ownership, and rollback are resolved by the existing generic-run behavior, persisted `EpicNumber` snapshot, EF migration boundary, and single-daemon deployment model.
