## ADDED Requirements

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

The `Remove` action on a linked-issue row SHALL NOT compete with the primary reading path on mobile viewport widths. It SHALL be placed in a secondary or overflow action affordance, and SHALL require a second explicit confirmation step before unlinking the issue from the epic. A single accidental tap on the row or on the Remove affordance SHALL NOT silently remove the issue–epic link.

#### Scenario: Remove lives in a secondary affordance, not the primary row

- **WHEN** a linked-issue row is rendered at a 390px viewport width
- **THEN** the `Remove` action SHALL be reachable through a secondary or overflow action rather than as an inline primary-row button
- **AND** SHALL NOT share the primary horizontal reading row with the issue number, title, and status/health

#### Scenario: Single tap on Remove does not unlink the issue

- **WHEN** a user taps the `Remove` affordance once on a linked-issue row at a 390px viewport width
- **THEN** the issue–epic link SHALL remain intact
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

When the Dependency Graph cannot be rendered — due to cyclic dependencies, empty data, or any other unrenderable state — the Epic detail page SHALL present a clear, user-facing explanation of why the graph is unavailable and SHALL keep the List view fully usable so the user can continue working with the linked issues.

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
