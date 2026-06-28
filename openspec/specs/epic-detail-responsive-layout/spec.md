### Requirement: Epic detail page has no horizontal overflow on mobile widths

The Epic detail page SHALL render without horizontal overflow at mobile viewport widths. For any epic status (`idle`, `running`, `paused`, `done`, `closed`), at viewport widths of 320px, 390px, and 430px, the page SHALL satisfy `documentElement.scrollWidth <= documentElement.clientWidth`. No fixed-width or `min-width` content (action button groups, badges, progress cards, the dependency graph, linked-issue rows) SHALL force the page wider than the viewport.

#### Scenario: Running epic does not overflow at mobile widths

- **WHEN** the Epic detail page is rendered for a `running` epic at a 390px viewport width
- **THEN** `documentElement.scrollWidth` SHALL be less than or equal to `documentElement.clientWidth`
- **AND** no horizontal scrollbar SHALL be present

#### Scenario: Idle epic does not overflow at mobile widths

- **WHEN** the Epic detail page is rendered for an `idle` epic at a 390px viewport width
- **THEN** `documentElement.scrollWidth` SHALL be less than or equal to `documentElement.clientWidth`
- **AND** the action button group SHALL not be clipped on the right edge

#### Scenario: Overflow constraint holds across the mobile width range

- **WHEN** the Epic detail page is rendered at viewport widths of 320px, 390px, and 430px
- **AND** the epic is in each of `idle`, `running`, `done`, and `closed`
- **THEN** `documentElement.scrollWidth` SHALL be less than or equal to `documentElement.clientWidth` at every combination

### Requirement: Epic detail header separates title and description from action buttons on mobile

On mobile viewport widths the Epic detail page header SHALL lay out as a single column: the epic title and description SHALL occupy the readable page width and SHALL NOT share a horizontal row with the action button group. Long Chinese and long English epic titles SHALL wrap within the available width and SHALL NOT be compressed into a per-character vertical column. The title and description SHALL keep a non-zero minimum width that allows readable wrapping at 320px.

#### Scenario: Title and description occupy full readable width on mobile

- **WHEN** the Epic detail page header is rendered at a 390px viewport width
- **THEN** the epic title element SHALL be laid out above (not beside) the action button group
- **AND** the action button group SHALL NOT reduce the title element's width below a readable wrapping width

#### Scenario: Long Chinese title wraps instead of stacking vertically

- **WHEN** the Epic detail page header is rendered for a `running` epic whose title is a long Chinese string at a 390px viewport width
- **THEN** the title SHALL wrap onto additional lines within the available width
- **AND** the title SHALL NOT render as a single-character-per-line vertical column

#### Scenario: Long English title wraps instead of overflowing

- **WHEN** the Epic detail page header is rendered for an epic whose title is a long unbroken English string at a 320px viewport width
- **THEN** the title SHALL wrap or break within the available width
- **AND** SHALL NOT cause horizontal overflow

### Requirement: Epic detail action buttons stay visible and unclipped on mobile

On mobile viewport widths the Epic detail page action buttons SHALL all remain reachable and unclipped. The primary lifecycle action SHALL remain directly visible: `Start Epic` for `idle`, `Pause` for `running`, and `Resume` for `paused`. Secondary actions (`Edit`, `Mark Done`, `Close Epic`) SHALL either wrap onto additional lines or collapse into a clear overflow entry, and SHALL NOT be clipped or hidden behind the viewport edge. `done` and `closed` epics SHALL NOT surface Start/Pause/Resume lifecycle actions.

#### Scenario: Primary lifecycle action stays visible by state on mobile

- **WHEN** the Epic detail page action button group is rendered at a 390px viewport width
- **THEN** an `idle` epic SHALL render the `Start Epic` action fully visible
- **AND** a `running` epic SHALL render the `Pause` action fully visible
- **AND** a `paused` epic SHALL render the `Resume` action fully visible

#### Scenario: Secondary actions remain reachable on mobile

- **WHEN** the Epic detail page action button group is rendered at a 390px viewport width for a non-terminal epic
- **THEN** the `Edit`, `Mark Done`, and `Close Epic` actions SHALL each be reachable without horizontal scrolling
- **AND** no action button SHALL be clipped by the viewport right edge

#### Scenario: Action buttons wrap rather than overflow on mobile

- **WHEN** the Epic detail page action button group is rendered at a 320px viewport width with multiple visible actions
- **THEN** the actions SHALL wrap onto additional lines or collapse into an overflow menu
- **AND** the button group SHALL NOT cause `documentElement.scrollWidth` to exceed `documentElement.clientWidth`

#### Scenario: Terminal epics show no lifecycle action on mobile

- **WHEN** the Epic detail page action button group is rendered at a 390px viewport width for a `done` or `closed` epic
- **THEN** no `Start Epic`, `Pause`, or `Resume` action SHALL be rendered

### Requirement: App header displays the real Epic number on project-prefixed routes

The application shell header SHALL display `Epic #<number>` (with the epic's actual number) on the Epic detail route, including the project-name-prefixed route (`/:projectName/epics/:id`). The header SHALL NOT render a bare `Epic #` with no number when the epic has loaded. While the epic is loading the header MAY display `Epic #…`; once loaded the header SHALL resolve and display the real number, falling back to a stable identifier (e.g. a short id prefix) only when no numeric epic number exists.

#### Scenario: Project-prefixed route shows the real epic number

- **WHEN** the header is rendered on the route `/:projectName/epics/:id` for an epic whose number is `3`
- **THEN** the header title SHALL be `Epic #3`
- **AND** SHALL NOT be a bare `Epic #`

#### Scenario: Project-prefixed route shows the real epic number when the path segment is numeric

- **WHEN** the header is rendered on the route `/:projectName/epics/:id` where the `:id` segment is the epic's number (`12`)
- **THEN** the header title SHALL be `Epic #12`

#### Scenario: Loading state then resolves to the real number

- **WHEN** the header is rendered on the Epic detail route while the epic is loading
- **AND** the epic then loads with number `7`
- **THEN** the header SHALL first display `Epic #…`
- **AND** SHALL then display `Epic #7`

### Requirement: Epic detail page preserves bottom spacing for the mobile bottom nav

The Epic detail page SHALL reserve bottom spacing so the fixed mobile bottom navigation (`MobileBottomNav`) does not obscure the linked issues region or other key content on mobile viewport widths. The page content SHALL remain fully scrollable and reachable above the bottom nav at 320px, 390px, and 430px widths.

#### Scenario: Linked issues region is not obscured by the bottom nav

- **WHEN** the Epic detail page is rendered at a 390px viewport width with linked issues present
- **THEN** the linked issues region SHALL be scrollable into view above the fixed bottom navigation
- **AND** the bottom navigation SHALL NOT permanently obscure the last linked issue row or its actions

### Requirement: Linked-issue rows render as mobile-first scannable task lines

At mobile viewport widths (320px, 390px, 430px) each linked-issue row in the Epic detail page SHALL render as a mobile-first task line that prioritizes, in reading order, the issue number, title, status/health, priority, and the start-blocker reason. The `Start` action SHALL appear only when the issue is actually startable and starting it would not violate the single in-progress rule. The row SHALL NOT cause horizontal overflow at any of 320px, 390px, or 430px; `Remove` and other destructive actions SHALL NOT occupy space in the primary reading path of the row.

#### Scenario: Linked-issue row is scannable at every mobile width

- **WHEN** the Epic detail page is rendered with linked issues at viewport widths of 320px, 390px, and 430px
- **THEN** each linked-issue row SHALL surface the issue number, title, status/health, priority, and start-blocker reason in that reading priority
- **AND** `documentElement.scrollWidth` SHALL NOT exceed `documentElement.clientWidth` at any of those widths

#### Scenario: Start action only renders when the issue is genuinely startable

- **WHEN** a linked-issue row is rendered at a 390px viewport width
- **THEN** the `Start` action SHALL NOT be rendered when the issue is blocked, already in progress, or would violate the single in-progress rule

#### Scenario: Long issue title wraps within the row instead of overflowing

- **WHEN** a linked-issue row with a long unbroken issue title is rendered at a 320px viewport width
- **THEN** the title SHALL wrap or break within the available row width
- **AND** SHALL NOT cause `documentElement.scrollWidth` to exceed `documentElement.clientWidth`

### Requirement: Linked-issue Remove action is relocated out of the reading path and requires confirmation

The `Remove` action on a linked-issue row SHALL NOT compete with the primary reading path on mobile viewport widths. It SHALL be placed in a secondary or overflow action affordance, and SHALL require a second explicit confirmation step before unlinking the issue from the epic. A single accidental tap on the row or on the Remove affordance SHALL NOT silently remove the issue-epic link.

#### Scenario: Remove lives in a secondary affordance, not the primary row

- **WHEN** a linked-issue row is rendered at a 390px viewport width
- **THEN** the `Remove` action SHALL be reachable through a secondary or overflow action rather than as an inline primary-row button
- **AND** SHALL NOT share the primary horizontal reading row with the issue number, title, and status/health

#### Scenario: Single tap on Remove does not unlink the issue

- **WHEN** a user taps the `Remove` affordance once on a linked-issue row at a 390px viewport width
- **THEN** the issue-epic link SHALL remain intact
- **AND** a confirmation step SHALL be presented before the unlink executes

#### Scenario: Remove only executes after explicit confirmation

- **WHEN** a user taps `Remove` and then confirms the prompted action on a linked-issue row at a 390px viewport width
- **THEN** the issue SHALL be unlinked from the epic
- **AND** if the user dismisses the confirmation, the link SHALL remain intact

### Requirement: Dependency Graph degrades clearly on mobile while keeping List reachable

At mobile viewport widths the Dependency Graph view SHALL offer a clear degradation path. It SHALL default to the List view, or present a horizontally scrollable graph, or display a "Graph works best on wider screens" message, and in every case the List view SHALL remain reachable. The Graph/List tab toggle SHALL remain visible, clickable, and understandable at 320px, 390px, and 430px widths.

#### Scenario: Graph defaults to a degraded presentation on narrow screens

- **WHEN** the Epic detail page is rendered at a 320px viewport width with a non-empty dependency graph
- **THEN** the graph region SHALL default to either the List view, a horizontally scrollable graph, or a "Graph works best on wider screens" prompt
- **AND** the List view SHALL be reachable from that state

#### Scenario: Graph/List tab toggle stays clickable and understandable on mobile

- **WHEN** the Epic detail page is rendered at viewport widths of 320px, 390px, and 430px
- **THEN** both the Graph tab and the List tab SHALL be visible and clickable
- **AND** switching tabs SHALL produce the corresponding view without error

#### Scenario: List remains the always-reachable fallback

- **WHEN** the Graph view is rendered in any degraded form at a 390px viewport width
- **THEN** the List view SHALL be available via the List tab without additional navigation or reload

### Requirement: Dependency Graph shows clear guidance and List fallback when unrenderable

When the Dependency Graph cannot be rendered - due to cyclic dependencies, empty data, or any other unrenderable state - the Epic detail page SHALL present a clear, user-facing explanation of why the graph is unavailable and SHALL keep the List view fully usable so the user can continue working with the linked issues.

#### Scenario: Cyclic dependencies surface an explanation and keep List usable

- **WHEN** the Epic detail page renders linked issues whose dependency graph contains a cycle
- **THEN** the graph region SHALL display an explanation that the graph cannot be rendered due to a dependency cycle
- **AND** the List view SHALL render the linked issues and remain usable

#### Scenario: Empty graph surfaces an explanation and keeps List usable

- **WHEN** the Epic detail page renders an epic whose dependency graph has no renderable data
- **THEN** the graph region SHALL display an explanation that there is nothing to render
- **AND** the List view SHALL remain reachable and usable

#### Scenario: Any other unrenderable state keeps List usable

- **WHEN** the dependency graph fails to render for a reason other than cycle or empty data
- **THEN** the graph region SHALL display a clear explanation that the graph is unavailable
- **AND** the List view SHALL remain the available fallback for continuing work
