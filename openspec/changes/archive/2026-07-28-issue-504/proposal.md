## Why

WorkflowRun currently stores its required Project, Issue, and Epic context as string entries in the user-facing annotations bag. This makes ownership and event lineage depend on runtime parsing, allows malformed or missing identity to fail late or silently, and leaves the documented WorkflowRun metadata model unimplemented.

## What Changes

- Store a WorkflowRun's Project ID, Issue number, and optional Epic number as typed metadata fields rather than system annotation entries.
- Preserve the existing lineage semantics: the fields remain WorkflowRun-local Issue context, WorkflowRun ownership and Issue association remain unchanged, and Epic affiliation continues to refresh from durable Issue events.
- Keep CloudEvent lineage extensions and API response fields unchanged while deriving them from the typed context.
- Reserve WorkflowRun annotations for user-defined metadata; newly written runs and migrated historical runs contain no system identity keys in annotations.
- Migrate persisted historical WorkflowRuns by transferring valid legacy annotation identity into typed metadata and retaining their existing operational lineage.
- Update the implementation-status note in `design/conventions.md` before implementation, then remove it when the documented metadata model is realized.

## Capabilities
- `workflow-run-lineage`: WorkflowRuns retain typed Project, Issue, and optional Epic context across creation, persistence, reload, and Epic-affiliation refresh; ownership checks and emitted lineage remain behaviorally stable, user annotations exclude system identity, and valid historical runs upgrade without losing their context.

## Impact

- **Server Workflow domain and grains:** `WorkflowRunMetadata`, `WorkflowRunLineage`, and `WorkflowGrain` use typed lineage context for creation, ownership, refresh, and event extension construction.
- **Server persistence:** WorkflowRun JSON serialization, SQLite computed lineage projections, and the existing state migration path must upgrade historical annotation-backed state while preserving query and index behavior.
- **Documentation:** `design/conventions.md` gains a temporary implementation-gap note until the specified representation is delivered.
- **Public behavior:** WorkflowRun API fields and workflow CloudEvent lineage attribute names and values remain unchanged; no dependency changes.
- **Tests:** Server coverage verifies typed-context persistence, legacy-state migration, annotation separation, ownership, Epic refresh, and unchanged emitted lineage.
