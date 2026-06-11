# OpenSpec Delta: web-ui — Kanban Cancelled Issues Visibility

## MODIFIED Requirements

### Requirement: Web UI uses cancelled as the user-facing term for closed issues

The Kanban board and related Web UI surfaces SHALL use `cancelled` as the user-facing term for closed issues, consistent with `IssueStatus.Cancelled`. Internal state names, component variables, test assertions, and rendered UI text SHALL NOT introduce `closed` / `showClosed` / "Closed" as a synonym for the cancelled state.

#### Scenario: Code variable naming uses cancelled
- **WHEN** the Kanban board component manages the visibility of cancelled issues
- **THEN** the relevant React state and helper functions are named with `cancelled` (for example `showCancelled` / `setShowCancelled`)
- **AND** the source SHALL NOT contain a `showClosed` state variable

#### Scenario: UI text uses cancelled
- **WHEN** the Kanban board renders a control to toggle visibility of cancelled issues
- **THEN** the control text contains the word `cancelled`
- **AND** the source SHALL NOT render a control whose label uses the term `Closed`

#### Scenario: Tests assert cancelled instead of Closed
- **WHEN** Kanban board tests verify that closed issues are not displayed
- **THEN** assertions SHALL use `cancelled` wording consistent with the rendered text
- **AND** tests SHALL NOT assert the absence of a `Closed` element as the mechanism that confirms cancellation behavior

### Requirement: Desktop Cancelled column renders its own toggle inside the column

The desktop Kanban board SHALL render the Cancelled column with an in-column toggle that controls whether the column body shows cancelled issues. The toggle SHALL be located inside the Cancelled column (column header area or column footer area), SHALL NOT be rendered next to the Done column, and SHALL update its own label to reflect the current state.

#### Scenario: Toggle is inside the Cancelled column
- **WHEN** the user views the desktop Kanban board with at least one cancelled issue
- **THEN** a toggle control is rendered as part of the Cancelled column
- **AND** the source SHALL NOT render a "Show cancelled" button as a sibling of the Done column on the desktop layout

#### Scenario: Toggle label reflects current state
- **WHEN** the Cancelled column body is hidden
- **THEN** the in-column toggle label contains `Show cancelled` and the issue count
- **WHEN** the user activates the in-column toggle to reveal cancelled issues
- **THEN** the in-column toggle label changes to `Hide cancelled`
- **AND** activating the toggle again hides the issues and restores the `Show cancelled` label

#### Scenario: No reverse toggle is missing
- **WHEN** cancelled issues are currently shown on the desktop board
- **THEN** a `Hide cancelled` control is reachable inside the Cancelled column
- **AND** the user can return the column to its hidden state without reloading the board

### Requirement: Cancelled column body is no longer cleared by the grouping layer

The `filterCancelledFromColumns` helper in the kanban grouping model SHALL NOT empty the Cancelled column's issues when the toggle is off. The Cancelled column SHALL always carry its full set of cancelled issues through the grouping pipeline, and visibility SHALL be controlled by the in-column rendering based on the toggle state rather than by deleting issues from the column.

#### Scenario: Grouping preserves cancelled issues when toggle is off
- **WHEN** `filterCancelledFromColumns` is called with `showCancelled = false`
- **THEN** the returned Cancelled column still contains all cancelled issues that were present on the input
- **AND** the Cancelled column's `issues` array is no longer replaced with an empty array

#### Scenario: Grouping is a no-op when toggle is on
- **WHEN** `filterCancelledFromColumns` is called with `showCancelled = true`
- **THEN** the returned column array is unchanged from the input

#### Scenario: Rendering decides visibility
- **WHEN** the Cancelled column carries issues and the toggle is off
- **THEN** the in-column render path hides the issues list and may show a clear "no issues shown" affordance
- **AND** toggling on re-renders the same issues from the unchanged column data

### Requirement: Mobile Cancelled tab count reflects the real number of cancelled issues

The mobile Kanban board SHALL compute the Cancelled tab count from the actual number of cancelled issues in the unfiltered-by-toggle column data. The Cancelled tab count SHALL NOT change when the user toggles `showCancelled` on or off.

#### Scenario: Cancelled tab count matches real issues
- **WHEN** the mobile board renders the Cancelled tab and there are 8 cancelled issues
- **THEN** the tab badge displays `8`
- **AND** the displayed count does not read `0` while the Cancelled column body is hidden

#### Scenario: Tab count is independent of showCancelled
- **WHEN** the user activates the mobile `Show cancelled` / `Hide cancelled` toggle
- **THEN** the Cancelled tab count remains the same
- **AND** counts on Backlog, In Progress, and Done tabs SHALL also remain unaffected by the cancelled toggle

#### Scenario: Mobile toggle is reachable from the list
- **WHEN** the user is viewing the Cancelled stage on mobile
- **THEN** a `Show cancelled` / `Hide cancelled` toggle is available inside the mobile list view
- **AND** activating it toggles whether the cancelled issues are rendered in the list body

### Requirement: Cancelled issue cards display a Cancelled status pill

The `IssueCard` component SHALL render the cancelled status pill for issues whose status indicator is `cancelled`. The previous exclusion of `indicator === 'cancelled'` from the pill render path SHALL be removed so cancelled issues carry a visible grey Cancelled status pill on the board.

#### Scenario: Cancelled indicator renders the pill
- **WHEN** an issue card is rendered for an issue with status indicator `cancelled`
- **THEN** the `StatusPill` for that indicator is rendered
- **AND** the source SHALL NOT contain a guard that excludes `indicator === 'cancelled'` from the pill render path

#### Scenario: Cancelled pill styling is grey
- **WHEN** a cancelled issue card displays its status pill
- **THEN** the pill uses grey styling consistent with the cancelled indicator branch in `StatusPill`
- **AND** the pill text reads `Cancelled`

#### Scenario: Other indicators continue to render their pills
- **WHEN** an issue card is rendered for an issue with indicator `blocked`, `approval`, `running`, `waiting`, or `drift`
- **THEN** the corresponding status pill still renders as before
- **AND** only the previous `cancelled` exclusion is removed; no other indicator behavior changes
