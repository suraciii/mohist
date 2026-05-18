## Why

Mohist users trust `Done` to mean that every promised workflow stage actually ran and produced completion evidence, but the current completion path can infer success from the absence of remaining work. This change hardens the WorkflowRun invariant so missing, empty, or lost task/check state blocks completion with clear recoverable evidence instead of silently advancing to `Done`.

## What Changes

- Require stage completion to compare `StageDefinition` promises with concrete `StageRun` evidence before a stage or workflow can pass.
- Prevent empty required task/check sets, missing static task/check runs, missing dynamic Build work, or unevaluated dynamic work sources from being treated as successful completion.
- Keep generated Build tasks and runtime-added work as run-owned `TaskRun` instances, and require all appended run work to complete successfully before checks, approval, stage pass, or final workflow pass.
- Require Check completion and approval to be backed by current authoritative verification, AI review, review verdict, and merge-readiness evidence.
- Require Integrate completion to preserve required integration task/check and delivery evidence before the issue can become `Done`.
- Apply the same completion guard from `nextWork()` to explicit stage completion paths so callers cannot bypass the invariant.
- Keep `WorkflowRunProjection` defensive: reject impossible passed snapshots such as workflows that did not reach the final stage, while avoiding stale AgentSession status or `mergeState` as the sole completion authority.
- Add regression coverage for empty static work, missing or zero dynamic Build work, pending runtime-added tasks, stale failed sessions after later success, and impossible passed projection snapshots.

## Capabilities

### New Capabilities


### Modified Capabilities

- workflow-run
- workflow-definition
- workflow-engine
- pipeline-model

## Impact

- Affects `packages/cli/src/workflow/domain/index.ts`, especially `StageDefinition`, `StageRun`, `WorkflowRun.nextWork()`, stage completion guards, dynamic Build task materialization, approval decisions, and runtime-added task handling.
- Affects workflow persistence and hydration under `packages/cli/src/workflow/domain/persistence.ts` and workflow run repositories because lost or missing stage evidence must not hydrate as successful completion.
- Affects stage execution in `packages/cli/src/workflow/workflow-engine.ts`, `packages/cli/src/workflow/config-driven-stage-runner.ts`, Build task loading, Check evidence flow, and Integrate delivery flow because runners must materialize or report required work before completion.
- Affects `packages/cli/src/services/workflow-run-projection.ts` and related issue projection/update paths so projected `Done` remains consistent with WorkflowRun evidence without treating stale sessions or merge state as authoritative completion truth.
- Affects regression tests in workflow domain, workflow engine/application service, Build task materialization, Integrate WorkflowRun, and projection coverage under `packages/cli/tests/`.
- No external API or dependency changes are expected; user-visible behavior changes from silent completion to blocked or failed workflow evidence when required work cannot be proven.
