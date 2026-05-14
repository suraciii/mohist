## Why

User-triggered rebase is currently executed through a side queue outside the visible WorkflowRun task list, so users cannot see that rebase is part of the current stage, cannot rely on normal task ordering/blocking semantics, and cannot tell why review or approval state changed afterward. This change is needed now because #199 is establishing the shared runtime-added task infrastructure that should become the single execution path for rebase instead of keeping a parallel queue-only workflow.

## What Changes

- Move the main Web/API rebase path from the issue task queue into the active WorkflowRun by appending a visible `rebase-branch` task to the current stage instead of executing rebase as a hidden `taskType='rebase'` job.
- Require `rebase-branch` to run through normal WorkflowRun scheduling so it respects stage task order, blocks later work while pending or running, and fails the current stage if rebase fails.
- Narrow the responsibility of `rebase-branch` to executing the rebase and reporting factual output about before/after base and head SHAs, including whether the candidate snapshot actually changed.
- Make review, check, and approval invalidation depend on the rebase result's SHA-change fact and stage policy, rather than on the user clicking Rebase or on rebase-specific side branches embedded in `AgentRunnerService`.
- Reuse the shared task handler and `StageContext` infrastructure delivered by #199 for this runtime-added workflow task rather than introducing a separate rebase execution mechanism.
- Preserve compatibility for any temporary legacy path only as a migration fallback; the visible WorkflowRun task path becomes the normal product behavior for Web UI and API initiated rebase.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `workflow-run`
- `workflow-engine`
- `http-api`
- `web-ui`

## Impact

- Workflow domain and execution code in `packages/cli/src/workflow/`, especially `workflow/domain/index.ts`, `workflow-engine.ts`, `stage-context.ts`, stage runners, and the shared task runtime introduced by #199.
- Existing rebase entrypoints and policy logic in `packages/cli/src/services/agent-runner-service.ts`, which currently execute rebase, emit rebase SSE progress, and mix in stage-specific reset/replan behavior.
- Issue rebase API behavior in `packages/cli/src/api/issues.ts`, which currently enqueues `rebase` into the issue task queue for non-Done stages.
- Stage-state projection and task explanation surfaces in `packages/cli/src/services/stage-state-service.ts` and related issue-detail data shaping so `Rebase branch` appears as an ordinary task with reason and caused-by metadata.
- Web UI issue workflow surfaces in `packages/cli/web/src/`, including the rebase action, task list rendering, and any rebase progress handling that currently depends on standalone SSE events instead of canonical WorkflowRun task state.
- Follow-up spec deltas for WorkflowRun task semantics, workflow-side SHA-change invalidation behavior, API rebase semantics, and UI expectations for visible runtime-added rebase work.
