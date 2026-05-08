## Why

Mohist stages currently complete with different and sometimes implicit health guarantees, so a user can be asked to approve or see an issue marked complete even though build, typecheck, or post-merge verification was skipped on that path. This change makes stage completion trustworthy by requiring explicit, machine-verifiable health gates at workflow boundaries before approval requests or final completion.

## What Changes

- Define an explicit health gate policy for each workflow boundary: plan, build, check, and done/post-merge.
- Run health gates before user approval checks so users approve artifacts and code that have already passed the configured machine verification.
- Generalize the existing build/test check path into a parameterized health check that records command, timeout, duration, concise failure summary, and log excerpt in stage execution check results.
- Keep existing `checks.buildTest` workflow configuration working by mapping it to the check-stage full verification gate when per-stage health gate configuration is absent.
- Make build-stage completion mean both task completion and successful configured build health verification.
- Make check-stage approval request contingent on the configured full verification gate passing.
- Require final post-merge verification before transitioning an issue to `done/completed` when final gates are enabled, and ensure direct merge APIs cannot bypass that verification.
- Preserve configurable policy strength so projects can choose lighter plan/post-merge gates without making every stage run the full test suite by default.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- `workflow-definition` — Stage behavior changes to include explicit per-stage health gates before approval and completion boundaries.
- `workflow-config` — Workflow configuration gains per-stage health gate policy while preserving existing `checks.buildTest` compatibility.
- `pipeline-model` — Stage progression and Done semantics change so machine health verification is part of the boundary contract, including post-merge verification before completion.
- `http-api` — Direct merge and approval-related paths must respect enabled final health gates and expose health gate failures rather than silently completing.
- `workflow-log` — Stage execution/check result visibility must include health gate command, duration, concise error summary, and log excerpt.

## Impact

- **Workflow checks**: `packages/cli/src/workflow/checks/build-test-check.ts`, `code-compiles-check.ts`, shared `Check`/`ReactionConfig` usage, and `BaseStageRunner` check sequencing need to support reusable parameterized health gates.
- **Stage runners**: `plan-stage-runner.ts`, `build-stage-runner.ts`, and `check-stage-runner.ts` need explicit health gate placement before `UserApprovalCheck` and before stage advancement.
- **Workflow config**: `packages/cli/src/workflow/workflow-loader.ts` needs per-stage health gate parsing/defaults and compatibility mapping from `checks.buildTest`.
- **Merge/completion**: `packages/cli/src/git/merge-queue.ts`, `workflow-engine.ts`, `agent-runner-service.ts`, server recovery/completion callbacks, and direct merge API handlers in `packages/cli/src/api/issues.ts` must route completion through post-merge health verification when enabled.
- **Persistence and visibility**: Existing stage execution check results should carry health gate metadata and failure excerpts; no separate lifecycle table is expected unless current storage proves insufficient.
- **User surfaces**: CLI/API/Web UI consumers of issue state and stage execution results may need copy and rendering updates to distinguish stage work completion from health gate pass/fail and approval readiness.
- **Tests**: Add or update workflow, config compatibility, stage runner, merge queue/direct merge, and API tests covering gate ordering, failure reporting, `checks.buildTest` fallback behavior, and prevention of final verification bypass.
