## Why

Check review failures currently expose ambiguous recovery actions and scattered task/check evidence, so users cannot tell whether Mohist already tried to repair review findings, whether another repair attempt is available, or why auto-fix stopped. This is needed now because real Check-blocked issues show completed repair tasks followed by failed follow-up reviews, which makes the UI look contradictory and makes `Retry` appear to mean "fix it again" when it may only retry the checkpoint.

## What Changes

- Project Check review repair state as structured workflow/stage-state data, including repair attempts used and allowed, repair availability, last repair task and status, follow-up review status, stop reason, and latest unresolved finding summary when available.
- Treat `fix-review-findings` completion as a repair-attempt outcome, not as review success; the authoritative review verdict remains the follow-up `review-passed` check.
- Update issue recovery actions for Check review failures so users can distinguish retrying a checkpoint, rerunning review only, and explicitly fixing review findings.
- Explain repair exhaustion in the issue page, including that auto-fix will not continue automatically once the repair budget is used.
- Make the Check review repair attempt limit come from one authoritative workflow policy instead of conflicting WorkflowRun and CheckStageRunner values.
- Add regression coverage for exhausted repair retry behavior and for completed repair followed by failed follow-up review display.

## Capabilities

### New Capabilities


### Modified Capabilities

- workflow-run
- http-api
- web-ui

## Impact

- Workflow runtime: `packages/cli/src/workflow/domain/index.ts`, `packages/cli/src/workflow/check-stage-runner.ts`, repair scheduling and retry/rerun paths must share one Check repair policy and preserve explicit repair-attempt facts.
- Stage-state/API projection: `packages/cli/src/services/stage-state-service.ts`, `packages/cli/src/api/issues.ts`, and related types must expose structured Check repair state through `GET /api/issues/:number/stage-state` while keeping `blockedReason` concise.
- Web UI: `packages/cli/web/src/components/IssueDetailPage.tsx`, `PipelineView`, `TaskProgressPanel`, API client/types, and related tests must render repair state and user-intent-specific actions without treating repair completion as review pass.
- Tests: backend workflow/API regression tests and frontend component tests need scenarios for exhausted repair budget, retry checkpoint behavior, and repair-completed plus follow-up-review-failed presentation.
