## Context

Rebase buttons are currently scattered across 3 locations in `IssueDetailPage.tsx`:
- **Build stage**: standalone button in Actions panel (line 639)
- **Review stage**: inside approval gate section (line 798)
- **Plan stage**: inside approval gate section (line 850)
- **Done stage**: handled by MergeStatePanel (separate, stays as-is)

The `interrupted` status has no rebase access because all three conditions require specific stage+approval combinations that exclude interrupted issues.

The existing rebase flow (`POST /api/issues/:number/rebase`) already has stage-specific post-rebase handlers (`handlePlanRebase`, `handleBuildRebase`, `handleReviewRebase`) and SSE progress events (`rebase_started`, `rebase_progress`, `rebase_completed`, `rebase_conflict`). This change adds a status query endpoint, a queue mechanism, and a unified UI panel — without modifying the existing rebase execution logic.

## Goals / Non-Goals

**Goals:**
- Expose worktree git state (ahead/behind) to the user for informed rebase decisions
- Consolidate all rebase UI into a single WorktreePanel visible for all stages with worktrees (including interrupted)
- Support queuing rebase when agent is running, auto-executing after agent completes
- Keep MergeStatePanel "Rebase and Retry" unchanged (Done stage merge flow)

**Non-Goals:**
- Modifying the existing rebase execution logic (fetch → canFastForward → rebaseOntoMaster → stage-specific post-actions)
- Adding rebase conflict resolution UI (abort-on-conflict behavior stays)
- Polling worktree status continuously (fetch on page load + SSE-driven refresh is sufficient)
- Persisting queued rebases across server restarts (in-memory only)

## Decisions

### D1: WorktreePanel placement — after MergeStatePanel, before Review Report

Panel order: Details → Pipeline Interrupted → Actions → MergeStatePanel → **WorktreePanel** → Review Report → Approval Required → Send Message → QuestionPanel → SessionList

**Rationale:** Worktree status is repository-level context, similar to merge state. Placing it after MergeStatePanel groups infrastructure-level panels together, keeping the approval/gate panels below. The panel is only visible when worktree exists, so it won't add visual noise for backlog/explore issues.

### D2: WorktreePanel visibility — driven by API response, not stage check

The panel's visibility is determined by `useWorktreeStatus(issueNumber)` returning `{ exists: true }`. The frontend does NOT replicate stage-based logic (stage !== Backlog/Explore). This avoids coupling the UI to stage names and handles edge cases (e.g., interrupted issues with worktrees from any stage).

**Alternatives considered:**
- Client-side stage filtering: simpler but duplicates logic and misses interrupted cases
- Conditional fetching: `enabled: issue.stage !== 'backlog' && issue.stage !== 'explore'` — avoids unnecessary API calls but couples panel visibility to stage names. Chosen as an optimization: fetch only when stage suggests worktree might exist, but let `exists` in the response control panel visibility.

### D3: Rebase queue — in-memory Map in route handler, triggered by EventBus listener

Queue storage: a `Map<number, { issueNumber, projectId }>` (issueNumber → queue entry) kept in closure scope within `createIssueRoutes`. When `POST /rebase?queue=true` is called while agent is running, the entry is stored in the map. An `agent_completed` EventBus listener checks the map and, if a queued rebase exists for that issue, calls the existing rebase logic directly (reusing the handler's internal functions).

**Rationale:** The queue is short-lived (exists only while agent runs, typically minutes to hours) and doesn't need persistence. Using EventBus listener keeps the mechanism decoupled from the agent runner internals. The map lives in the same closure as the rebase handler, so it has access to all dependencies (`worktreeManager`, `agentRunner`, `issueService`, etc.).

**Alternatives considered:**
- Database table: overkill for a transient queue, adds migration complexity
- AgentRunnerService-level queue: would couple rebase logic into agent infrastructure
- Frontend-only queue (re-call API after `agent_completed` SSE): works but adds race conditions and network dependency

### D4: `getWorktreeStatus()` uses `git rev-list --left-right --count`

Implementation: `git rev-list --left-right --count <baseBranch>...<issueBranch>` returns `<ahead>\t<behind>`. Combined with existing `isRebaseInProgress()` method.

**Rationale:** Single git command, O(1) for common cases (git uses commit graph). The `canFastForward` field is derived as `behind === 0` (no commits to pull from base).

### D5: `useWorktreeStatus` hook — no polling, SSE-driven refresh

The hook uses React Query with `queryKey: ['worktree-status', issueNumber]`, no `refetchInterval`. Status is refreshed when:
1. Component mounts (initial fetch)
2. `rebase_completed` / `rebase_conflict` SSE events invalidate `['issues']` — we add `['worktree-status', issueNumber]` to this invalidation
3. After rebase mutation succeeds (local invalidation in mutation callback)

**Rationale:** Worktree status changes are always triggered by rebase actions (which produce SSE events) or agent completions. Polling is unnecessary and wastes resources.

## Risks / Trade-offs

- **[Queue lost on server restart]** → Acceptable. User can re-request rebase. Queue is advisory, not a commitment.
- **[Race condition: agent completes between queue check and queue write]** → The `agent_completed` listener processes synchronously. If agent completes before queue write, the queue entry sits orphaned. Mitigation: in the `agent_completed` listener, also check if agent is still running before executing queued rebase (it won't be, but the rebase handler itself will work fine since agent is no longer running). Orphaned entries are harmless — they just get ignored on next check.
- **[Worktree status fetch latency on page load]** → `git rev-list` is fast (<100ms for typical repos). If worktree is on NFS or slow disk, add a loading skeleton. Low risk.
- **[WorktreePanel adds visual height to sidebar]** → Panel is compact (branch name + 1 line status + 1 button). Acceptable trade-off for unified rebase access.

## Migration Plan

No database changes, no API breaking changes. Deploy sequence:

1. Add `getWorktreeStatus()` to `WorktreeManager` (backend, no UI impact)
2. Add `GET /:number/worktree-status` route and queue support to rebase route (backend)
3. Add `useWorktreeStatus` hook and `WorktreePanel` component (frontend)
4. Add WorktreePanel to IssueDetailPage sidebar
5. Remove 3 stage-specific rebase button blocks from IssueDetailPage
6. Add `['worktree-status', issueNumber]` to SSE invalidation for rebase events

Steps 1-3 can be deployed independently. Steps 4-5 must be deployed together (otherwise rebase UI disappears before WorktreePanel appears). Step 6 should accompany step 4.

**Rollback:** Revert to stage-specific rebase buttons. New API endpoints return 404 safely if frontend doesn't call them.

## Open Questions

- Should WorktreePanel poll when agent is running (to detect external pushes to master)? Current design relies on manual refresh or rebase-triggered updates. For M1, no polling is acceptable.
