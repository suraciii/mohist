## Why

Integrate is now the only runnable stage that still bypasses Mohist's standard `Task`/`Check` contract, so its work is largely invisible to `workflow_run` persistence and the Web UI. This change is needed now because issue #182 already established the shared runtime model, and Integrate remaining outside that model leaves the final stage as the one place users cannot see real progress, failures, or recovery state.

## What Changes

- Standardize Integrate on the same `BaseStageRunner` lifecycle used by Plan, Build, and Check: task execution first, then persisted checks and shared failure handling.
- Convert the current hardcoded Integrate steps into standard stage tasks so `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` are written to `workflow_tasks` and exposed as live progress.
- Reclassify final post-merge verification from an ad hoc runner-side step into a standard Integrate check so `final-health` is recorded in `workflow_checks` and participates in normal recheck/fix policy flow.
- Seed Integrate task and check definitions in `workflowRunService` so the active workflow run has a complete stage contract before execution starts, matching how Plan already behaves.
- Make Integrate inherit shared `CheckFailurePolicy`, retry, and approval-aware semantics from `BaseStageRunner` instead of reimplementing partial step bookkeeping inside the runner.
- Preserve existing side effects and ordering for spec sync, change archive, merge, and final verification while making their status, duration, and failures visible through the same persistence and UI pathways as other stages.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `workflow-run` - Integrate task and check state becomes first-class `WorkflowRun` data rather than hidden runner-local progress.
- `web-ui` - Issue detail and pipeline views must show Integrate task/check progress and final verification status in real time.
- `workflow-definition` - Integrate stage behavior changes from ad hoc step execution to the standard task/check framework, including final health as a formal check.
- `pipeline-model` - Integrate gains the same visible runtime lifecycle and local check-failure handling semantics as other stages.

## Impact

- **Workflow runners**: `packages/cli/src/workflow/integrate-stage-runner.ts` must stop manually owning step bookkeeping and instead map Integrate work onto `executeTasks()`, `getChecks()`, and standard check-failure policy hooks from `packages/cli/src/workflow/base-stage-runner.ts`.
- **Workflow run persistence**: `packages/cli/src/services/workflow-run-service.ts` and `packages/cli/src/db/workflow-run-repo.ts` must seed and update Integrate task/check rows so `workflow_tasks` and `workflow_checks` reflect the active final-stage contract.
- **Stage-state mirroring**: `packages/cli/src/services/stage-state-service.ts` and BaseStageRunner mirroring paths must stop treating Integrate as an empty static stage so current-state task/check views stay aligned with WorkflowRun data.
- **Checks and recovery policy**: Integrate needs explicit check metadata and `CheckFailurePolicy` mapping for `final-health` and related fix tasks so post-merge verification failures can use the same retry/recheck machinery as other stages.
- **Frontend progress surfaces**: `packages/cli/web/src/components/TaskProgressPanel.tsx`, `PipelineView.tsx`, related query hooks, and workflow-run types must render seeded Integrate tasks/checks instead of falling back to an empty or opaque stage.
- **Behavioral safety**: Existing spec sync, archive, merge, and final verification side effects must remain unchanged in ordering and outcome while gaining standardized persistence, status updates, and evidence visibility.
