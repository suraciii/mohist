## Context

IssueDetailPage currently has a single 851-line component that renders all sections inline. The Changes panel (lines 417–524) is gated by `DIFF_STAGES` (line 265) and positioned after Comments (line 415). The panel reuses `useIssueDiff` and `useIssueCommits` hooks which already fetch data regardless of stage — the hooks are called unconditionally at lines 137/162. The only stage-dependent part is the render guard `showDiff && ...`.

Key constraint: no API changes needed. The data is already being fetched for all stages; we just need to render it.

## Goals / Non-Goals

**Goals:**
- Remove `DIFF_STAGES` gate so Changes panel renders in every stage
- Move the Changes panel JSX block from after Comments to after Description (before TaskList)
- Add a summary statistics line above the tabs
- Show "No changes yet" empty state when no files or commits exist

**Non-Goals:**
- No new API endpoints or hooks
- No changes to DiffViewer, CommitRow, or diff rendering logic
- No inline changes summary in approval panels (optional scope, defer)
- No extraction of Changes panel into a separate component file (keep it inline like the current implementation)

## Decisions

### D1: Keep Changes panel as inline JSX in IssueDetailPage

The current Changes panel (~100 lines of JSX) is an IIFE block inside IssueDetailPage. Rather than extracting it to a new component file, keep it inline and simply move the block. This minimizes diff size and avoids introducing new component boundaries that need prop-typing.

**Alternatives considered:** Extract to `ChangesPanel.tsx` component — cleaner long-term but adds scope (prop interface, new file, import changes) for what is fundamentally a repositioning change. Can be done as a follow-up refactor.

### D2: Summary statistics computed from existing data

Compute summary from `diffData.files` and `commitsData.commits` which are already fetched. Aggregate `additions`/`deletions` from the files array. Display as a single line above the tabs: `N files changed, +X, -Y, M commits`.

**Alternatives considered:** Add a server-side summary endpoint — overkill since the data is already on the client.

### D3: Empty state renders the panel with a message instead of returning null

Currently the IIFE returns `null` when `files.length === 0 && commits.length === 0` (line 420). Change this to render the panel container with "No changes yet" text. This ensures the panel is visually present in all stages, giving users a consistent anchor point.

### D4: Defer inline approval summary to follow-up

Adding a compact changes summary to PlanApprovalPanel/ReviewApprovalPanel is marked optional in the acceptance criteria. Deferring keeps this change focused on repositioning.

## Risks / Trade-offs

- [Loading flash in Backlog] `useIssueDiff` and `useIssueCommits` are called unconditionally but will 404 or return empty for Backlog issues. The hooks already handle this gracefully (return empty data), so the panel will just show the empty state. No actual risk.
- [No visual regression in other sections] Since we're only moving JSX blocks and not changing their internal rendering, other sections (Comments, TaskList, sidebar) are unaffected. Verify by checking layout at each stage.

## Migration Plan

No migration needed. This is a pure frontend layout change with no data or API changes. Deploy in a single PR.

## Open Questions

None.
