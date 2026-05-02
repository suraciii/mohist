## Context

The Changes panel (diff/commits viewer) in `IssueDetailPage.tsx` is currently rendered at lines 417–524, gated by `DIFF_STAGES` (line 25, line 265), and positioned after Comments in the main column. The data hooks (`useIssueDiff`, `useIssueCommits`) are already unconditionally called at lines 137 and 162 — they fetch regardless of stage. The only gate is the JSX render condition `showDiff && ...` and the early-return `if (files.length === 0 && commits.length === 0) return null` inside the IIFE.

The entire change is a single-file layout refactor in `IssueDetailPage.tsx` — no new components, no API changes, no state changes.

## Goals / Non-Goals

**Goals:**
- Remove `DIFF_STAGES` constant and `showDiff` variable — Changes panel renders unconditionally
- Move the Changes JSX block from after Comments (line 417) to after Description (line 352), before TaskList (line 354)
- Add a one-line summary stats header inside the Changes panel (computed from existing `diffData`/`commitsData`)
- Show "No changes yet" empty state when both files and commits are empty (instead of returning null)

**Non-Goals:**
- No new API endpoints or data hooks
- No changes to DiffViewer, CommitRow, or diff rendering
- No inline changes summary in approval panels (deferred — marked optional in proposal)
- No file search/filter additions

## Decisions

### D1: Keep Changes panel inline in IssueDetailPage (don't extract component)

The diff/commits JSX block (~100 lines) shares state with the parent (`diffTab`, `expandedCommits`, `expandedFiles`, `diffData`, `commitsData`). Extracting it would require prop-drilling 6+ values or a context. Since this is a layout-only change, the cost of extraction isn't justified.

**Alternatives considered:**
- Extract to `ChangesPanel` component — cleaner separation but adds props interface for no behavioral gain in this change

### D2: Replace IIFE null-return with explicit empty state

Current code uses `(() => { ... if (empty) return null ... })()` which silently hides the panel when there's no data. Replace with a direct render that shows an empty state card when no data exists, making the panel always visible.

**Alternatives considered:**
- Keep IIFE pattern and just remove the stage gate — would still hide the panel in Backlog, violating the "visible in all stages" requirement

### D3: Summary stats computed inline from existing data

Total additions/deletions are computed by reducing over `diffData.files`. Commit count is `commitsData.commits.length`. No new queries or memoization needed — the data is already fetched and the reduction is trivial (< 100 files typically).

### D4: Defer inline changes summary in approval panels

The optional acceptance criterion to add a compact summary in PlanApprovalPanel/ReviewApprovalPanel adds cross-component coupling (sidebar components would need diff/commits data). Defer to a follow-up change if user feedback indicates it's needed — the main panel reposition already puts changes in view during approval reviews.

## Risks / Trade-offs

- [Extra API calls in Backlog stage] → `useIssueDiff` and `useIssueCommits` already fire unconditionally (lines 137, 162). The queries will 404 or return empty for issues without worktrees, which is the existing behavior. No new load.
- [Empty state card visual noise in Backlog] → The "No changes yet" card is minimal (single line text, same border style as other sections). Acceptable trade-off for consistent panel presence.

## Migration Plan

Single deploy — the change is purely frontend layout. No API contract changes, no database migrations, no config changes. Rollback is reverting the JSX order and restoring `DIFF_STAGES` check.
