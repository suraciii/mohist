## Context

KanbanBoard currently groups issues by their `stage` field directly. When a user closes an issue, `IssueService.close()` sets `status=Closed` but leaves `stage` untouched, so the closed issue stays in its original column (Plan, Build, etc.). IssueCard has a secondary bug: `Blocked` status maps to `'closed'` badge type, producing a gray overlay labeled "Closed" — conflating two semantically distinct states.

Three components need changes: `KanbanBoard.tsx` (grouping logic), `IssueCard.tsx` (badge rendering), and `App.tsx` (toggle state). StageColumn's existing Done-column collapse mechanism stays unchanged.

## Goals / Non-Goals

**Goals:**
- Closed issues appear only in the Done column (display-layer redirect)
- "Show closed" toggle defaults to off, hiding closed issues in Done
- Blocked gets a red/orange badge; Closed gets a gray badge; neither uses an overlay
- Reopen restores issue to original column with zero backend changes

**Non-Goals:**
- No backend changes (no `stage` mutation on close/reopen)
- No persistence of toggle state (resets on page load)
- No changes to StageColumn collapse logic
- No batch close/reopen operations

## Decisions

### D1: Display-layer redirect via KanbanBoard grouping

In `KanbanBoard.tsx`, the `useMemo` that builds columns will check `issue.status === IssueStatus.Closed`. If true, the issue goes into the Done bucket regardless of `issue.stage`.

**Alternatives considered:**
- *Backend stage mutation on close*: Would require updating `stage` to `Done` on close and restoring on reopen. Riskier — needs new backend logic, potential for stage corruption, and reopen must remember the original stage. Rejected because display-only redirect is zero-risk.
- *Separate "Closed" column*: Would add a 6th column not backed by the workflow model. Rejected because Done already serves as the archive area and has collapse logic.

### D2: Toggle lives in KanbanBoard as local state

`showClosed` is a `useState(false)` inside `KanbanBoard`. The board passes filtered issue lists to StageColumn for the Done column. This keeps the toggle scoped to the board — no prop drilling through App.

**Alternatives considered:**
- *Toggle in App.tsx / URL param*: Over-engineered for a transient UI preference. Not persisted, not shared between routes.
- *Toggle per column*: Unnecessary — only Done column contains closed issues.

### D3: IssueCard BadgeType split — 'blocked' and 'closed' as separate types

Replace the current `'closed'` badge type (which is misused for Blocked) with two distinct types: `'blocked'` (red/orange) and `'closed'` (gray). Remove the overlay entirely — both states render as a badge in the card's badge area.

**Current bug:** `getBadgeType()` returns `'closed'` for `IssueStatus.Blocked` (line 33). The Badge component excludes `'closed'` from its union type, so it never renders a Badge for this case. Instead, `isClosed` triggers a gray overlay at line 104-108.

**Fix:** Add `'blocked'` and `'closed'` as separate BadgeType variants. Blocked → orange/red badge. Closed → gray badge. Remove overlay logic.

### D4: Done column count reflects visible count

The tab bar count (mobile) and column header count (desktop) should reflect the total issues including closed, even when toggle is off. This lets users know closed issues exist without seeing them. The filter only affects which IssueCards render inside StageColumn.

## Risks / Trade-offs

- [Mobile tab bar count includes hidden closed issues] → Acceptable: the count signals "something is in Done", and the toggle is one tap away. If confusing, can subtract closed count later.
- [Closed issue in Done column gets same collapse treatment as completed issues] → Acceptable and desirable: the existing `DONE_COLLAPSE_LIMIT=5` further reduces noise.
- [StageColumn receives pre-filtered issues] → StageColumn's `totalCount` will differ from the actual array length when toggle is off. Toggle filtering should happen inside KanbanBoard before passing to StageColumn, so StageColumn always gets a consistent array.

## Migration Plan

Pure frontend change. No database migration, no API changes. Deploy in a single PR. Rollback is safe — removing the redirect just restores current behavior.

## Open Questions

None.
