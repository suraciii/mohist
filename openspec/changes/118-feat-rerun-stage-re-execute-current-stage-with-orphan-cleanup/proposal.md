## Why

When an agent process crashes unexpectedly (exitCode=null), coder sessions remain stuck in `running` state and the issue stalls at whatever stage it was in — with no agent actually running. Existing recovery options are inadequate: **reopen** doesn't clean orphan sessions or restart the pipeline, **retry** only works in `blocked` state, and **restart** resets all progress back to backlog. Users need a way to re-execute the current stage from scratch without losing prior work.

## What Changes

- New API endpoint `POST /issues/:number/rerun` — cleans orphan coder sessions, clears current stage checkpoint, resets approval/blocked state, and resumes pipeline from the current stage
- Works in all issue states (active, interrupted, blocked, closed) — also reopens closed issues
- Preserves committed files in worktree, plan artifacts, and outputs from completed stages
- Frontend: "Rerun Stage" button on IssueDetailPage and IssueCard for all non-running states

## Capabilities

### New Capabilities

- `rerun-stage` — Re-execute the current pipeline stage with orphan cleanup

### Modified Capabilities

- `http-api` — New `POST /issues/:number/rerun` endpoint
- `web-ui` — Rerun button on IssueDetailPage and IssueCard

## Impact

- `packages/cli/src/api/issues.ts` — new rerun route handler
- `packages/cli/src/services/agent-runner-service.ts` — expose `cleanupOrphanedCoderSessions` for reuse
- `packages/cli/web/src/lib/api.ts` — new `rerunIssue()` client method
- `packages/cli/web/src/components/IssueDetailPage.tsx` — Rerun button UI
- `packages/cli/web/src/components/IssueCard.tsx` — Rerun shortcut button
