## Why

Workflow variables are currently fixed at dispatch time, so tasks cannot produce dynamic values for downstream tasks to consume. This forces hardcoded paths such as `openspec/changes/issue-{N}` and blocks workflows where one task computes a value that later tasks need. Now is the right time because the runner and WorkflowRun already pass an `output` field through task results; we only need to capture declared outputs and expose them in the existing `${{ }}` template chain.

## What Changes

- Add an optional `outputs` array to task definitions. Each entry declares `{ name, from }` where `from` is a JSONPath-style selector into the action result `output` JSON.
- Extend the runner to parse declared outputs from a successful `ActionResult.output` and include them in `WorkResult`.
- Add a runtime variable store to `WorkflowRun`, keyed by `${{ tasks.<taskDefinitionId>.outputs.<name> }}`.
- Update `MakeDispatchAsync` to deep-merge the runtime variable store into the existing variable resolution chain after dispatch injection and before final resolution.
- Extend template resolution so `${{ tasks.<id>.outputs.<name> }}` resolves in subsequent task `with`, `artifacts`, and other templated fields.
- Persist runtime variables across stages within the same workflow run.
- Preserve existing behavior for tasks without `outputs` and ensure failed tasks produce no output variables.

## Capabilities

### New Capabilities
- `task-output-variables`: Declare, capture, and resolve task output variables at runtime via `${{ tasks.<id>.outputs.<name> }}`.

### Modified Capabilities
- `workflow-definition`: Task definition schema gains optional `outputs` array with `{ name, from }` entries.
- `workflow-engine`: Task result processing extracts declared outputs and stores them in the WorkflowRun runtime variable store; dispatch variable resolution merges runtime variables into the existing chain.
- `workflow-run`: WorkflowRun aggregate owns a runtime variable store scoped to the run and persists it across stages.

## Impact

- Workflow YAML parser, task definition models, and validation.
- Runner TypeScript types and result handling (`ActionResult.output`, `WorkResult`).
- Server-side `WorkflowRun`, `StageRun`, and `MakeDispatchAsync` variable construction.
- Template resolution service to support the `tasks.<id>.outputs.<name>` namespace.
- No changes to cross-workflow variable sharing, external runtime injection, or the existing `openspecChangeDir` hardcoding.
