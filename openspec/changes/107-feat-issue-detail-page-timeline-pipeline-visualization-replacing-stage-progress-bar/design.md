## Context

mohist is an AI-driven development workflow automation tool. The current IssueDetailPage shows only a **state snapshot** (stage, status, tasks, comments) via a horizontal progress bar. Users returning after stepping away cannot quickly understand what happened during their absence — they must mentally piece together SessionList + TaskList + approval_state + workflow_log.

The pipeline metaphor (Plan → Build → Review → Done) is mohist's core, yet the UI fails to visualize it as such.

**Constraints:**
- Pure frontend implementation — no backend API changes
- All data already exists in existing endpoints
- Must integrate with existing SSE event system for real-time updates
- Must be mobile-responsive

## Goals / Non-Goals

**Goals:**
- Replace the horizontal stage progress bar with a vertical pipeline timeline on IssueDetailPage
- Show the complete narrative from issue creation to current state
- Support three-level collapsible details (stage summary → round/task details → session link)
- Real-time updates via SSE events with RAF-based throttling
- Reuse existing components where possible (SessionTimeline, useSessionTimeline)

**Non-Goals:**
- Creating new backend APIs (all data inferred client-side from existing endpoints)
- User time vs AI time distinction (not relevant for single-user mode)
- Multi-user collaboration features

## Decisions

### D1: Vertical timeline replaces horizontal progress bar

The existing horizontal stage progress bar (lines 352-380 in IssueDetailPage.tsx, the `<div className="mb-6">` block containing `STAGES.map`) is replaced entirely with a vertical `IssueTimeline` component below the issue title. This is the user's first visual anchor point — vertical orientation better expresses "how we got here" vs horizontal's "where we are".

**Alternatives considered:**
- Keep horizontal bar and add timeline as secondary element → rejected (adds page complexity without solving the core UX problem)
- Timeline in right sidebar → rejected (disrupts existing layout and the narrative flow should be near the title)

### D2: Three-level collapsible information hierarchy

```
Level 1 (default): Stage name + status icon + duration
Level 2 (on click): Round labels (Plan) or task list (Build) + "View session →"
Level 3 (link):    Navigate to SessionPage for full agent detail
```

This matches three user mental modes: "quick scan" → "evaluate quality" → "deep dive".

**Alternatives considered:**
- Only two levels (summary + expand) → rejected (no path to deep dive)
- Modal for Level 3 → rejected (disrupts flow; direct navigation is cleaner)

### D3: `useIssueTimeline` hook aggregates three data sources

```typescript
// Data sources:
useIssue(issueNumber)           // createdAt, approval_state
useCoderSessions(issueNumber)   // stage sessions (plan/build/review)
api.getWorkflowLogs(issueNumber) // filtered to plan/build events
```

The hook constructs a sorted timeline array and subscribes to SSE events for live updates.

**Alternatives considered:**
- Create separate hooks per data source and compose in component → rejected (duplicates timeline inference logic, harder to maintain SSE subscription)
- Query the existing `GET /api/issues/:number/logs` with event type filter → the API doesn't support filtering, so we fetch all and filter client-side

### D4: RAF-based throttling for SSE event processing

High-frequency events (e.g., 500+ `ralph_task_update` events in 3 seconds) could lock the UI if processed per-event. The hook uses `requestAnimationFrame` to batch updates every 100ms.

```typescript
// Simplified pattern
let pendingUpdates: Event[] = []
let rafId: number | null = null

function scheduleUpdate() {
  if (rafId !== null) return
  rafId = requestAnimationFrame(() => {
    processUpdates(pendingUpdates)
    pendingUpdates = []
    rafId = null
  })
}
```

**Alternatives considered:**
- Debounce with fixed delay → rejected (RAF batching is more predictable for animation frames)
- Throttle with setInterval → rejected (doesn't align with render cycle)

### D5: Reuse SessionTimeline's round inference logic

`useSessionTimeline.ts` already has `reconstructRoundsFromLogs()` which infers plan rounds from workflow logs. This logic is reused via import rather than duplicated.

**Alternatives considered:**
- Duplicate the round inference in `useIssueTimeline` → rejected (code duplication, divergence risk)

### D6: Timeline node types and Stage enum mapping

Timeline labels are user-facing abstractions that map to underlying data sources:

```
Timeline Label    → Data Source                              Stage Enum
─────────────────────────────────────────────────────────────────────────
Created           → issues.createdAt                         (event, not a stage)
Plan              → coder_session(stage=plan)                 Stage.Plan
Approved          → issues.approvalState.requestedAt          (approval gate event)
Build             → coder_session(stage=build)                Stage.Build
Review            → coder_session(stage=check)                Stage.Check  ← "Review" is display label for Stage.Check
Done              → inferred from Stage.Done                  Stage.Done
Pending stages    → inferred from STAGE_ORDER, no timestamps
```

**Note:** `Stage.Explore` and `Stage.Draft/Backlog` are intentionally omitted from the timeline. The timeline starts with "Created" (issue creation timestamp) and the Explore phase is considered part of the pre-Plan workflow, not a distinct pipeline stage visible to the user. This simplification keeps the timeline focused on the core pipeline: Plan → Build → Review → Done.

Duration is computed as `completedAt - createdAt` for completed stages.

## Risks / Trade-offs

[Risk] SSE event subscription cleanup → The hook must properly unsubscribe on unmount or issue change. Use `onAgentEvent` which returns an unsubscribe function; call it in a `useEffect` cleanup.

[Risk] High-frequency task updates during Build stage → Mitigation is the RAF throttling (D4). Test with 500+ events verifies UI remains responsive.

[Risk] API doesn't support log filtering → We fetch all logs and filter client-side to event types relevant for timeline (`plan`, `build_started`, `build_completed`, `task_started`, `task_completed`, `task_failed`, `build_failed`).

[Risk] Mobile layout → The vertical timeline naturally adapts to narrow viewports. Ensure no horizontal scrolling and legible text at <768px.

## Migration Plan

1. **Create `useIssueTimeline.ts`** — new hook aggregating issue + sessions + workflow logs
2. **Create `IssueTimeline.tsx`** — timeline UI component with three-level collapse
3. **Modify `IssueDetailPage.tsx`** — replace lines 352-380 (horizontal progress bar, the `<div className="mb-6">` block containing `STAGES.map`) with `<IssueTimeline issueNumber={issueNumber} />`
4. **Add SSE subscriptions** — hook listens for `plan_round_start`, `plan_round_complete`, `ralph_task_update`, `build_started`, `build_completed`
5. **Test** — verify real-time updates during active issue, verify mobile layout at 375px width
6. **Remove old progress bar** — confirm no other component depends on the horizontal bar

Rollback: Revert the change in IssueDetailPage.tsx and delete the new files.

## Open Questions

- Should the timeline show "Created" node even for issues that have been running (vs starting from a session start)?
- What duration format is preferred: "8m 26s" or "8m" for simplicity?
- Should pending stages show estimated wait time or just be gray/hollow?
