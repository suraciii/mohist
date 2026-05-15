## Why

Active issues can spend a long time in Plan, Build, or Check while the project base branch advances through other completed deliveries, leaving candidates and their review or merge-ready evidence silently tied to an old base position. Mohist already has rebase and merge-ready safety mechanisms, but users need earlier product-level drift awareness and safe rebase scheduling so stale evidence and conflicts are surfaced before Check approval or Integrate.

## What Changes

- Track the base position observed by active issue candidates and compare it with the current project base when the base branch advances.
- Introduce base drift as a non-failure issue state that can be displayed for Plan, Build, Check, and pre-delivery Integrate candidates.
- Produce a rebase opportunity decision for drifted candidates: `skip`, `suggest`, `enqueue`, `defer`, or `needs-attention`.
- Defer automatic rebase while mutating agent work is running, then reconsider at safe windows such as stage idle, approval wait, or task boundaries.
- Schedule rebase through the visible `rebase-branch` WorkflowRun task in the current stage rather than changing the issue branch as a hidden background side effect.
- Invalidate stale Check review, merge-ready, and approval evidence when base drift or rebase makes the evidence no longer trustworthy.
- Prevent Check approval from appearing actionable when it is backed by stale evidence; guide the user toward rebase and rerun checks instead.
- Surface drift state, rebase recommendation or pending decision, defer reason, and conflict failure details in CLI and Web UI issue surfaces.
- Add regression coverage for Check evidence invalidation after base advancement and Build-task protection until a task boundary.

## Capabilities

### New Capabilities

- `base-drift-awareness`: Active issue candidates expose observed base position, drift status, rebase opportunity decision, safe-window handling, and user attention state.

### Modified Capabilities

- `workflow-run`: Runtime-added rebase work and evidence invalidation behavior expand from user-triggered rebase to base-drift-driven rebase opportunities and stale approval prevention.
- `workflow-engine`: Stage execution and approval preparation account for base drift, protect mutating work, schedule `rebase-branch` only at safe windows, and block Check approval on stale evidence.
- `http-api`: Issue list/show and stage-state responses expose drift status, rebase decision, defer reason, stale evidence state, and rebase conflict diagnostics.
- `cli-interface`: `mo issue show <number>` and relevant issue list output display base drift, recommended or pending rebase decisions, needs-attention state, and conflict next actions.
- `web-ui`: Issue cards, Issue Detail, approval surfaces, stage task/check progress, and attention summaries show drift/rebase decision state and suppress stale Check approvals.
- `event-bus`: Workflow events include base advancement, drift detection, rebase opportunity, safe-window, decision, scheduling, rebase completion, evidence invalidation, and user-attention notifications as needed for live UI updates.

## Impact

- `packages/cli/src/workflow/domain/` and `packages/cli/src/workflow/*-stage-runner.ts` - model drift-driven decisions, safe-window checks, task-boundary scheduling, and Check approval blocking.
- `packages/cli/src/workflow/task-runtime/rebase-task-handler.ts` - continue using the existing visible `rebase-branch` task while carrying enough base/head/conflict evidence for drift resolution.
- `packages/cli/src/services/workflow-application-service.ts`, `workflow-run-projection.ts`, and `stage-state-service.ts` - project drift, rebase decisions, stale evidence, and user-attention state into read APIs.
- `packages/cli/src/git/worktree-manager.ts` - provide or reuse base/head/merge-base facts needed to compare observed candidate base against current project base.
- `packages/cli/src/api/issues.ts` and related API types - expose drift and rebase decision details through issue list/show, stage-state, and approval flows.
- `packages/cli/src/cli/commands/issue.ts` - render drift, rebase recommendation/defer reason, stale approval guidance, and conflict diagnostics.
- `packages/cli/web/src/components/IssueCard.tsx`, `IssueDetailPage.tsx`, `PipelineView.tsx`, `WorktreePanel.tsx`, and attention summary utilities - show drift/rebase status and hide or replace stale approval actions.
- `packages/cli/src/services/event-bus.ts` and `packages/cli/web/src/hooks/useSSE.tsx` - propagate new drift and rebase-opportunity events for live refresh.
- Backend and frontend regression tests - cover base advanced while Check is awaiting approval and base advanced while Build mutating work is running.
- No new rebase API entrypoint, final squash merge strategy, or concrete database schema is specified by this proposal.
