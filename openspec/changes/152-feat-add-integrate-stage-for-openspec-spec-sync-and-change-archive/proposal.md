## Why

Mohist can already use `openspec/changes/<change>/` as the execution package for an issue, but completion does not reliably update `openspec/specs/` or make integration visible as a first-class workflow boundary. This leaves Done issues as “approved/merged somewhere” rather than a trustworthy canonical state where code, main specs, archived change history, and final verification all agree.

## What Changes

- Add an explicit `Integrate` stage between `Check` and `Done`, making the workflow boundary `Check approve -> Integrate -> Done` visible in backend state and UI pipeline surfaces.
- Change Check semantics so Check answers whether the candidate is acceptable and ready to integrate, including build/test validation, AI review, user approval, spec-sync dry run, delta conflict detection, and mergeability/rebase feasibility checks.
- Move deterministic integration work out of hidden Done/post-merge side effects and into the Integrate stage: main spec sync, OpenSpec change archive, candidate merge/rebase, final integration health gate, and Done transition.
- Introduce deterministic OpenSpec delta application from `openspec/changes/<change>/specs/**/spec.md` into `openspec/specs/<capability>/spec.md`, with validation for added, modified, removed, and renamed requirements.
- First implement the spec sync service as a reusable foundation before wiring Check dry-run and Integrate apply, so later workflow tasks consume one parser/apply contract instead of duplicating requirement-block logic.
- Ensure OpenSpec change archival only happens after successful spec sync, and distinguish OpenSpec change archive from issue archive or worktree cleanup.
- Re-home the existing post-merge health gate as Integrate final verification so failures block Done with visible evidence instead of acting as an implicit completion side effect.
- Prevent Integrate from invoking agent conflict resolution, coder build fixes, or other product-code behavior changes; failed spec sync, archive, merge, or final health verification must leave the issue blocked or interrupted at Integrate for user intervention.
- Preserve integration evidence on successful Done issues, including spec sync summary, archive path, merge truth, and final health gate result.

## Capabilities

### New Capabilities

- `openspec-integration` — Deterministic synchronization of approved delta specs into main specs, OpenSpec change archival ordering, and integration evidence semantics.

### Modified Capabilities

- `pipeline-model` — Pipeline order and stage semantics change from Plan/Build/Check/Done to Plan/Build/Check/Integrate/Done, with Done meaning integration completed.
- `workflow-definition` — Check no longer archives changes or completes issues directly; Integrate owns spec sync, change archive, merge/rebase, final verification, and Done transition.
- `change-artifacts` — OpenSpec change archive becomes an Integrate action that is blocked by failed spec sync and remains distinct from issue archive/worktree cleanup.
- `workflow-config` — Final health gate configuration remains supported but is interpreted and surfaced as Integrate final verification rather than hidden post-merge completion work.
- `workflow-log` — Stage execution/check result records must expose Integrate step results, final health command metadata, summaries, and log excerpts.
- `pipeline-session-events` — Event/SSE surfaces must represent Integrate progress and failure steps so issue detail views can update live.
- `http-api` — Issue, approval, merge, status, and agent-status APIs must report Integrate stage/state and must not allow approval or direct merge paths to bypass Integrate verification.
- `web-ui` — Issue cards, detail pages, approval panels, and pipeline views must show Integrate as a distinct stage and avoid displaying integrating or failed-integration issues as Done.
- `session-timeline-ui` — Issue detail timeline must preserve and render integration evidence after completion.

## Impact

- **Workflow state model**: `packages/cli/src/types/index.ts`, `STAGE_ORDER`, `STAGE_TRANSITIONS`, issue status/recovery rules, and any stage validation must add `Stage.Integrate` and route `Check -> Integrate -> Done` while preserving reject/failure loops back to Build or blocked/interrupted states.
- **Stage runners and engine**: `packages/cli/src/workflow/workflow-engine.ts`, `check-stage-runner.ts`, `BaseStageRunner`, runner registration, approval handling, checkpoint behavior, and recovery paths must stop treating Check approval as completion and add an Integrate runner/service.
- **OpenSpec services**: A new integration service should read change delta specs, dry-run and apply deterministic requirement changes to `openspec/specs/`, produce impact/sync summaries, and call the existing archive directory move only after sync succeeds.
- **Current implementation progress**: T-001 has landed the initial `OpenSpecIntegrator` service and focused unit tests. T-006 has moved final verification into `IntegrateStageRunner`, preserving `healthGates.postMerge` as the compatibility config key while recording results as `health:integrate`; subsequent tasks should route approval/recovery/API/UI through that Integrate contract.
- **Merge/finalization**: `packages/cli/src/git/merge-queue.ts`, `worktree-manager.ts`, `services/post-merge-finalizer.ts`, `agent-runner-service.ts`, and direct merge/API paths must move hidden merge/finalizer behavior under Integrate and disable agent conflict/build-fix behavior during Integrate.
- **Health gates**: `packages/cli/src/workflow/workflow-loader.ts` and `checks/health-gate-check.ts` must keep existing post-merge gate value while recording and presenting it as Integrate final verification.
- **Persistence and evidence**: Stage execution records, check results, merge state, blocked reason, comments or artifacts, and archived change contents need enough structure to show failing step, command/duration/log excerpt, spec impact, archive path, base/head/merge commit, and final result.
- **API and events**: Issue detail/status APIs, approval endpoints, merge retry/direct merge endpoints, agent status recovery, EventBus event types, and SSE registrations must expose Integrate progress, readiness, evidence, and failures.
- **Frontend**: Pipeline/Issue Detail components, issue cards, approval panel, session/integration timeline, API client types, hooks, and copy need to render Integrate steps, readiness previews, failure reasons, and Done evidence.
- **Tests**: Add or update unit/integration tests for stage transitions, Check readiness dry runs, deterministic spec sync validation, archive ordering, no-agent-fix Integrate failures, merge/final health blocking, API state reporting, and UI rendering of Integrate/Done evidence.
