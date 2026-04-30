## Context

The mohist frontend currently has no way to display worktree branch status (ahead/behind counts). The `WorktreeManager` class in `packages/cli/src/git/worktree-manager.ts` has methods like `exists()`, `isRebaseInProgress()`, and `canFastForward()`, but no method that returns ahead/behind counts. There is no REST endpoint exposing worktree status, and the frontend has no `useWorktreeStatus` hook.

Rebase buttons are embedded in three locations within `IssueDetailPage.tsx` (lines 640-660 Build stage, 803-835 Review gate, 855-886 Plan gate), each duplicating the same `rebaseMutation` logic and result display.

Key existing infrastructure:
- **Rebase endpoint**: `POST /:number/rebase` already exists at `issues.ts:2003` with SSE events (`rebase_started`, `rebase_progress`, `rebase_completed`, `rebase_conflict`)
- **Rebase allowed stages**: `[Stage.Plan, Stage.Build, Stage.Review, Stage.Done]` (`issues.ts:1933`)
- **`WorktreeManager`**: Has `exists()`, `isRebaseInProgress()`, `getPath()` — but no `getWorktreeStatus()` method
- **Frontend**: `api.rebaseIssue()` already defined in `api.ts:193`, mutation pattern in `IssueDetailPage.tsx:230-253`

## Goals / Non-Goals

**Goals:**
- Add `GET /api/issues/:number/worktree-status` endpoint returning ahead/behind counts, rebase-in-progress state
- Add `useWorktreeStatus` hook and `api.getWorktreeStatus()` client method
- Create self-contained `BranchBar` component with three visual states (synced, behind, rebasing)
- Remove all three inline rebase button blocks from IssueDetailPage
- BranchBar owns rebase mutation internally — IssueDetailPage no longer manages `rebaseMutation` or `rebaseResult`

**Non-Goals:**
- No changes to the existing `POST /:number/rebase` endpoint behavior
- No changes to SSE event structure
- No conflict resolution UI beyond displaying conflicting file names
- No polling optimization beyond simple `refetchInterval`

## Decisions

### D1: Add `getWorktreeStatus()` to WorktreeManager (not inline in API route)

Add a new method to `WorktreeManager` that computes ahead/behind via `git rev-list --left-right --count` and checks rebase state via existing `isRebaseInProgress()`. The API route delegates to this method.

**Why:** Follows existing pattern — `WorktreeManager` owns all git operations, API routes are thin handlers. Keeps the route handler under 30 lines.

**Alternatives considered:**
- Inline git commands in the API route: breaks the git encapsulation pattern used everywhere else
- Separate `WorktreeStatusService`: over-engineering for a single method

### D2: BranchBar is a self-contained component with its own mutation

BranchBar owns the rebase `useMutation` internally. IssueDetailPage removes `rebaseMutation` state, `rebaseResult` state, and all three rebase button blocks.

**Why:** The whole point of this change is to consolidate rebase logic into one place. If BranchBar receives mutation callbacks as props, the parent still owns the logic — defeating the purpose.

**Alternatives considered:**
- BranchBar receives `onRebase` callback: parent still manages mutation state, partial duplication remains
- Extract shared `useRebase()` custom hook: unnecessary abstraction — one component uses it

### D3: Place endpoint alongside other `/:number` sub-routes

Hono uses Trie-based routing — `/:number/worktree-status` is more specific than `/:number` and matches correctly regardless of registration order. Place it near `GET /:number/diff` (line 1220) which follows the same sub-resource pattern.

**Why:** All existing `/:number/*` sub-routes (diff, commits, logs, build-status, tasks, etc.) are registered after `GET /:number` and work correctly. Hono matches the most specific path first.

**Alternatives considered:**
- Register before `GET /:number`: unnecessary — Hono's Trie router handles specificity correctly

### D4: Polling interval 30 seconds via React Query `refetchInterval`

Use `refetchInterval: 30_000` on the `useWorktreeStatus` query. Also invalidate the query key on rebase mutation completion for instant refresh.

**Why:** Ahead/behind counts change when master gets new commits or after a rebase. 30s is frequent enough to catch external pushes without excessive API calls. Rebase completion triggers immediate refetch.

**Alternatives considered:**
- SSE-driven updates: would require backend pushing ahead/behind changes on every fetch — over-engineering
- No polling, only manual refresh: user won't notice the branch fell behind

## Implementation Steps

### Step 1: Backend — `WorktreeManager.getWorktreeStatus()`

Add method to `packages/cli/src/git/worktree-manager.ts`:

```typescript
interface WorktreeStatus {
  exists: boolean
  branch?: string
  baseBranch?: string
  ahead?: number
  behind?: number
  rebaseInProgress?: boolean
  conflictingFiles?: string[]
}
```

Method uses `git rev-list --left-right --count <baseBranch>...<branch>` in the project path (not worktree path — the baseBranch ref is tracked there). Uses existing `isRebaseInProgress()` for rebase state. If rebase in progress, uses `getConflictingFiles()` (already private) — expose as public or inline the logic.

### Step 2: Backend — API route

In `packages/cli/src/api/issues.ts`, add `GET /:number/worktree-status` alongside other sub-resource routes like `GET /:number/diff` (around line 1220). Standard pattern: validate project context → find issue → get project baseBranch → call `worktreeManager.getWorktreeStatus()` → return JSON.

### Step 3: Frontend — API client

Add `getWorktreeStatus(number)` to `packages/cli/web/src/lib/api.ts` — simple `request()` call to `/issues/${number}/worktree-status`.

### Step 4: Frontend — `useWorktreeStatus` hook

Add to `packages/cli/web/src/hooks/useQueries.ts`:

```typescript
export function useWorktreeStatus(issueNumber: number, enabled: boolean) {
  return useQuery({
    queryKey: ['issues', issueNumber, 'worktree-status'],
    queryFn: () => api.getWorktreeStatus(issueNumber),
    enabled: enabled && issueNumber > 0,
    refetchInterval: 30_000,
  })
}
```

### Step 5: Frontend — `BranchBar` component

New file `packages/cli/web/src/components/BranchBar.tsx`:

- Props: `issueNumber: number`, `stage: Stage`, `isAgentRunning: boolean`, `baseBranch: string`
- Uses `useWorktreeStatus(issueNumber, BRANCH_BAR_STAGES.has(stage))` internally
- Uses `useMutation({ mutationFn: () => api.rebaseIssue(issueNumber) })` internally
- Three visual states based on `data.behind`, `data.rebaseInProgress`, `data.conflictingFiles`
- Renders nothing (returns null) when `data?.exists === false` or stage not in `[Plan, Build, Review, Done]`

### Step 6: Frontend — Integrate into IssueDetailPage

In `packages/cli/web/src/components/IssueDetailPage.tsx`:

1. **Add**: `<BranchBar>` import and render at line ~406, before the Description section (inside `<div className="lg:col-span-2 space-y-6">`)
2. **Remove**: `rebaseMutation` definition (lines 230-253)
3. **Remove**: `rebaseResult` state (lines 224-228)
4. **Remove**: Build stage rebase button block (lines 640-660)
5. **Remove**: Review approval gate rebase block (lines 803-835)
6. **Remove**: Plan approval gate rebase block (lines 855-886)

## Risks / Trade-offs

- **[Route ordering]** ~~Hono matches `/:number/worktree-status` against `/:number`~~ → Mitigation: Hono uses Trie-based routing, more specific paths match first regardless of registration order
- **[git command performance]** `git rev-list --left-right --count` runs in the main repo on every status request → Mitigation: 30s polling interval is conservative; the command is O(commits in range) which is typically small
- **[Stale ahead/behind without fetch]** Counts are based on local refs, may be stale if `smartFetch` hasn't run recently → Mitigation: acceptable for display purposes; rebase endpoint itself calls `smartFetch` before acting
- **[BranchBar invisible during agent build]** If agent is running, rebase is disabled but BranchBar still shows status → Acceptable: user can see the branch is behind even if they can't act on it

## Open Questions

None. The design reuses all existing infrastructure (rebase endpoint, WorktreeManager patterns, API patterns).
