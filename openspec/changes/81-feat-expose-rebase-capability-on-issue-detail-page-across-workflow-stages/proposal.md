## Why

Rebase capability is fully encapsulated inside MergeQueue and only triggers automatically after agent completion. Users have no way to manually sync their issue branch to the latest master at any stage — leading to stale designs (plan), inaccurate diffs (review), silent drift accumulation (build), and an opaque "Retry Merge" button (done) that doesn't communicate that rebase is involved.

## What Changes

- Add `POST /api/issues/:number/rebase` endpoint with precondition checks (worktree exists, agent not running, stage in {plan, build, review, done})
- Stage-specific rebase behavior:
  - **Plan**: rebase → trigger agent re-self-review with updated master context → refresh approval gate output
  - **Build**: rebase → clear build checkpoint (task outputs may be invalidated) → user manually resumes pipeline
  - **Review**: rebase → build verify → update Changed Files diff → abort on conflict
  - **Done**: delegate to existing MergeQueue retry flow (equivalent to current retry-merge)
- Add "Rebase onto master" button to IssueDetailPage actions panel for plan, build, and review stages
- Rename "Retry Merge" → "Rebase and Retry" in MergeStatePanel
- Rebase progress feedback via SSE: checking → rebasing → verifying → complete (or conflict list)

## Capabilities

### New Capabilities

- `issue-rebase-api` — `POST /api/issues/:number/rebase` endpoint with stage-aware precondition checks and differentiated post-rebase behavior
- `issue-rebase-ui` — "Rebase onto master" button in IssueDetailPage across plan/build/review stages, SSE progress feedback, and MergeStatePanel rename

### Modified Capabilities

- `worktree-manager` — `canFastForward()` and `rebaseOntoMaster()` are already available from change #73; this change consumes them from the API layer
- `http-api` — new `POST /api/issues/:number/rebase` route registered in issue routes
- `web-ui` — new rebase button in IssueDetailPage, MergeStatePanel text change, SSE event handling for rebase progress

## Impact

- **packages/cli/src/api/issues.ts**: New rebase route handler with stage-specific logic
- **packages/cli/src/services/event-bus.ts**: New rebase event types in EventMap
- **packages/cli/src/api/events.ts**: New event types in ALL_EVENT_TYPES array
- **packages/cli/src/git/worktree-manager.ts**: Consumed (no new methods expected beyond #73)
- **Web UI IssueDetailPage.tsx**: New "Rebase onto master" button with stage-conditional visibility
- **Web UI MergeStatePanel.tsx**: Rename "Retry Merge" → "Rebase and Retry"
- **Web UI api.ts**: New rebaseIssue API method
- **Web UI useSSE.ts**: New rebase event subscriptions
