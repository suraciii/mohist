## Context

The Kanban board (`packages/web/src/widgets/kanban-board/`) currently has several UX breaks around the Cancelled column that were accumulated during the early development of the `IssueStatus.Cancelled` terminal state. Five concrete problems exist:

1. **Terminology drift.** Internal state is `showClosed` (`KanbanBoard.tsx:478`), helper is `filterClosedFromDone` (`kanban-grouping.ts:29`), and a derived count is `closedCount` (`kanban-grouping.ts:44`). The UI text reads "Show cancelled" (`:586`). Tests in `kanban-board-query.test.tsx:565` assert `screen.queryByText('Closed').not.toBeInTheDocument()` to prove cancellation behaviour, even though the page never renders a literal "Closed" element. The product term is `IssueStatus.Cancelled`; the code uses a different word.

2. **Default-empty Cancelled column with off-column toggle.** `filterClosedFromDone` (`kanban-grouping.ts:29-42`) wipes the Cancelled column's `issues` array when `showClosed === false`. The column header is still rendered as a 4th desktop column, but the body is empty. To make the issues visible, the user has to click a button rendered **next to the Done column** (`KanbanBoard.tsx:618-629`) — outside the Cancelled column they are trying to expand. There is no way to hide the column again once expanded.

3. **Mobile tab count is wrong.** Mobile renders `{col.issues.length}` (`KanbanBoard.tsx:572`) for each tab badge. Because `displayedColumns` has already been run through `filterClosedFromDone`, the Cancelled tab shows `0` even when 8 cancelled issues exist. Toggling the visibility state changes the tab count, which is not what a tab count should do.

4. **Cancelled card carries no status pill.** `IssueCard.tsx:264` has `indicator && indicator !== 'cancelled' && !isIntegrateWithFailure && (<StatusPill .../>)`. The `StatusPill` component already knows how to render a grey "Cancelled" pill (`IssueCard.tsx:63-72`) but the render guard skips it. The card's overlay ("Cancelled" stamp at `IssueCard.tsx:239-245`) is the only signal.

5. **No reverse control.** The current `Show cancelled (n)` link at `KanbanBoard.tsx:586` / `:626` only opens; once open there is no `Hide cancelled` affordance — the user is stuck with a non-empty column on the desktop board and has to refresh.

Stakeholders:
- **End users** browsing the board on desktop and mobile. They need a single word ("cancelled"), a working tab count, and a reversible toggle.
- **Mobile-first users** who rely on the tab counts to navigate. They are currently misled.
- **Developers** who read the code and tests; the term should match the domain (`IssueStatus.Cancelled`).

Constraints:
- No backend changes — `IssueStatus` enum, API responses, and persisted state are out of scope.
- No changes to Backlog / InProgress / Done column rendering.
- The change must remain a Web UI-only delta; existing tests must be updated to use the new naming and behaviour.

## Goals / Non-Goals

**Goals:**
- One term — `cancelled` — across React state, helper functions, test assertions, and UI text.
- The Cancelled column always carries its full set of issues; visibility is a render decision, not a grouping mutation.
- Desktop: a `Show cancelled` / `Hide cancelled` toggle that lives **inside** the Cancelled column and supports both directions.
- Mobile: the Cancelled tab count reflects the real number of cancelled issues and is **not** affected by the toggle state; the mobile list view offers an equivalent in-list toggle.
- Cancelled issue cards render a grey `Cancelled` status pill from `StatusPill` (in addition to the existing full-card overlay, which is not removed).

**Non-Goals:**
- No change to the `IssueStatus.Cancelled` value, semantics, or backend modelling.
- No change to Backlog / InProgress / Done column rendering, the archive flow, or the workflow stage pill.
- No new issue status.
- No persistence of the toggle to URL or local storage (it remains local session state for now).
- No visual redesign of the Cancelled column beyond placing the toggle and showing the status pill.

## Decisions

### D1. Rename `showClosed` → `showCancelled` everywhere

**What.** Rename the React state, helper parameter, test setup, and any other references. The helper `filterClosedFromDone` is renamed to `filterCancelledFromColumns` and its second parameter to `showCancelled`. The derived return value `closedCount` is renamed to `cancelledCount`.

**Why.** Terminology consistency with `IssueStatus.Cancelled`. Once the column "no longer clears" (D2), the `FromDone` suffix is also misleading — the function only deals with the Cancelled column. `filterCancelledFromColumns` describes the actual behaviour (cancelled column kept; visibility is the renderer's job).

**Alternatives considered.**
- *Keep `showClosed` as an internal name and only fix the UI text.* Rejected: the spec (`specs/web-ui/spec.md` Scenario "Code variable naming uses cancelled") and the issue body both explicitly call out internal naming as a defect. The test assertion `queryByText('Closed').not.toBeInTheDocument()` is currently vacuous; renaming forces a real assertion.
- *Rename to `cancelled` (drop the `show` prefix).* Rejected: `showCancelled` reads better as a boolean and matches React-state conventions elsewhere in the file (`mobileFiltersOpen`, `expanded` in `StageColumn`).

### D2. Stop mutating column data; let the renderer decide visibility

**What.** `filterCancelledFromColumns` becomes effectively a no-op — it returns the input unchanged. The Cancelled column's `issues` array always holds all cancelled issues through the grouping pipeline. `StageColumn` (or a thin in-column wrapper) reads `showCancelled` and either renders the issues list or shows an empty-state affordance.

**Why.** The current behaviour is a "model does the rendering" inversion: a domain-pure function silently empties a column to express a UI affordance. That makes:
- tab counts wrong (D3),
- test setup awkward (you have to know to pass `true` to `filterClosedFromDone` to see the issues),
- and the test assertion `queryByText('Closed').not.toBeInTheDocument()` misleading (it asserts on a string that is not the one the UI uses).

Separating data (column contents) from view (visibility) fixes all three at once.

**Alternatives considered.**
- *Make `filterCancelledFromColumns` always return columns unchanged AND delete the helper.* Rejected: keeping the symbol documents intent and leaves a clear seam if visibility decisions later move back to the data layer (e.g. for URL persistence). The helper is now a documented identity, not a no-op-by-accident.
- *Introduce a `visible: boolean` flag on each column.* Rejected: over-engineering. React state in the renderer is the natural place for transient UI state.

### D3. Mobile tab count is computed from unfiltered columns

**What.** The mobile tab bar iterates `allColumns` (the output of `groupIssuesByStage(issues)`) for badge counts, **not** `displayedColumns`. The `cancelledCount` (renamed `closedCount`) is also computed from `allColumns` so the mobile `Show cancelled (n)` link in the list view shows the real number.

**Why.** The count is the user's only signal of "there is something hidden here". Showing `0` when there are 8 cancelled issues is the exact bug the issue calls out. Decoupling count from visibility is the minimum fix.

**Concretely.** The existing `getDoneColumnCounts` is renamed to `getCancelledColumnCount` (it only ever read the Cancelled column) and is called against `allColumns` instead of `filteredColumns`. The mobile tab map switches to `allColumns` for the count text. The list body still iterates `displayedColumns` so that toggling `showCancelled` actually hides the cards in the mobile list.

**Alternatives considered.**
- *Memoize counts separately and only recompute when `issues` changes.* The current code already memoizes `getDoneColumnCounts` with `useMemo`. The change is just the dependency: feed it `allColumns` instead of `filteredColumns`.
- *Compute the count in the render.* Rejected: `useMemo` keeps it cheap and consistent with the rest of the file.

### D4. Move the desktop toggle inside the Cancelled column

**What.** The `Show cancelled (n)` button currently rendered next to the Done column (`KanbanBoard.tsx:618-629`) is removed. `StageColumn` gains an optional `headerExtra` slot, and `KanbanBoard` passes a `Show/Hide cancelled` toggle for the Cancelled column only. When the toggle is off and there are cancelled issues, the column body shows an empty-state line (e.g. "Cancelled issues hidden — show?") plus the toggle. When the toggle is on, the body renders the issues normally and the toggle reads `Hide cancelled`.

**Why.** The button being next to Done is the spatial lie the issue calls out: the affordance for expanding a column is not attached to that column. Putting it in the column header (or as a footer button inside the column body) is the obvious fix and matches the spec ("in-column toggle ... column header area or column footer area").

**Implementation sketch.** `StageColumn` already has a header row (`StageColumn.tsx:81-97`) and a sort bar (`StageColumn.tsx:99-117`). The toggle is added as a third element in the header, or as a footer row before the existing "archived" footer (the latter for the Done column). To keep `StageColumn` agnostic of the Cancelled concept, the toggle is passed in as `headerToggle?: ReactNode` and `footerToggle?: ReactNode`. The `Cancelled` branch in `KanbanBoard` passes the toggle; `Done` and others pass nothing.

**Alternatives considered.**
- *Render the toggle in `KanbanBoard` itself, after the `StageColumn` for Cancelled.* Rejected: that re-introduces the "outside the column" placement that the issue rejects. The visual target should be the column.
- *Add a new `CancelledStageColumn` component.* Rejected: the column rendering is otherwise identical to other stages; one toggle slot is enough.

### D5. Mobile keeps an in-list toggle

**What.** The existing mobile `Show cancelled` link at `KanbanBoard.tsx:579-589` is kept, with `closedCount` → `cancelledCount` and the `showClosed` → `showCancelled` rename. When the user has activated the toggle, the label becomes `Hide cancelled`. The tab count and the in-list toggle count are both derived from `allColumns` (D3) so the count does not change when the toggle is flipped.

**Why.** Mobile is a list view; there is no column header where to drop a toggle. The in-list placement is already there and the spec calls it out as a requirement.

### D6. Cancelled card renders the cancelled status pill

**What.** Remove `indicator !== 'cancelled'` from the render guard at `IssueCard.tsx:264`. The `StatusPill` already has a grey `Cancelled` branch (`IssueCard.tsx:63-72`) which is now reachable. The existing full-card overlay ("Cancelled" stamp at `IssueCard.tsx:239-245`) is kept.

**Why.** The overlay alone is too coarse — it greys the entire card and stamps "Cancelled" in the middle, which competes with the title. A small grey pill in the header row is a more glanceable signal that does not break scannability.

**Why keep the overlay too.** The overlay communicates "this card is dormant / not actionable" at a glance across a long list; the pill is a status signal. They serve different purposes. The spec says "the pill uses grey styling consistent with the cancelled indicator branch in `StatusPill`" — it does not ask to remove the overlay.

**Alternatives considered.**
- *Replace the overlay with just the pill.* Rejected: the overlay is the only visual cue that the card is non-actionable (no rerun button, dimmed). The pill alone would not communicate that as clearly.
- *Use a different colour for the pill.* Rejected: spec requires grey; consistency with the existing `StatusPill` branch.

### D7. Test updates mirror the rename and the new behaviour

**What.**
- `kanban-grouping.test.ts`: `filterClosedFromDone` import + describe block + assertions are updated to `filterCancelledFromColumns`. The "hides cancelled issues when `showClosed` is false" test is replaced with an assertion that the helper is now a no-op (`expect(result).toBe(columns)`) and that the input is preserved. `getDoneColumnCounts` → `getCancelledColumnCount` and its describe block.
- `kanban-board-query.test.tsx:543-567` ("reveals cancelled issues after clicking the show cancelled control"): the assertion `queryByText('Closed').not.toBeInTheDocument()` is dropped or rewritten — the test must assert that clicking the toggle reveals the issues, **not** that a "Closed" element is absent (which would still pass even if the toggle is broken). Concretely, the test now asserts:
  1. The toggle button initially reads `Show cancelled (1)` and is **inside** the Cancelled column (locate via `data-testid="stage-column-cancelled"` and `within(...)`).
  2. After clicking, the cancelled issue renders and the button label changes to `Hide cancelled`.
  3. After clicking again, the issue disappears and the button reads `Show cancelled (1)`.
  4. The mobile test (or a new test) renders 8 cancelled issues and asserts the tab badge shows `8` regardless of toggle state.

**Why.** The current test "passes" by accident because `Closed` never appears. The new tests actually pin down the behaviour the user observes.

**Alternatives considered.**
- *Add a new test file.* Rejected: the existing file is the right home; the rename is consistent and the file already covers toggle-adjacent cases.

## Risks / Trade-offs

- **Test refactor is brittle if the column `data-testid` is changed.** → The Cancelled column already renders as `data-testid="stage-column-cancelled"` (`StageColumn.tsx:75`) — use that as the test anchor; do not add new ids.
- **`filterCancelledFromColumns` becoming a no-op could be misread as dead code.** → Add a one-line JSDoc explaining "identity function kept as a render/decide-visibility seam; do not reintroduce issue mutation here". Specs already encode the intent.
- **Mobile tab count no longer matches the visible list length.** → This is the correct behaviour. A tab count should answer "how many exist?", not "how many are visible?". The issue body calls this out as a bug, not a feature. Document it in the in-component comment so a future reader does not "fix" it.
- **Toggle state is local to the session.** → If the user reloads, the column collapses again. This is acceptable for now (the change is "default collapsed, toggle to expand") and matches the spec. URL persistence is non-goal.
- **The Cancelled status pill plus the overlay plus the dimmed text could feel redundant.** → The overlay uses `bg-muted-foreground/40` and the pill is `bg-gray-200`; they are visually distinct. If user feedback later calls this out, the overlay is the easier one to drop. Out of scope for this change.

## Migration Plan

This is a Web UI-only change with no data migration. Deploy steps:

1. Land code + test changes in one PR.
2. Run `npm run build:web` and the full test suite (`npm test` in `packages/web`).
3. Manual smoke: open the board on desktop with at least one cancelled issue, click the in-column toggle twice, confirm the cancelled tab on mobile shows the real count and the in-list toggle works.
4. Roll back by reverting the commit — no schema or API changes, so a revert is clean.

No feature flag: the change is small and behaviour-equivalent in the "default off" case (cancelled issues are still hidden by default on desktop), and improves the mobile count from day one.

## Open Questions

- **Should the Cancelled status pill also show in non-board contexts (issue detail, list views)?** The spec only constrains the Kanban board. Out of scope, but worth a follow-up issue if the term inconsistency repeats there.
- **Should the toggle persist to URL (`?showCancelled=1`) so a shareable link preserves "open" state?** Not in this change; deferred.
- **The Done column also has a "collapse to 5" pattern (`StageColumn.tsx:13, 42-46`).** The Cancelled column's toggle is similar in spirit but separate. Should they share a "show more" abstraction? Not in this change — the Cancelled toggle is hide/visible, Done's is pagination. Different mechanisms, keep them apart.
