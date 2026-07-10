### Requirement: Desktop core status groups are reachable by default

At a common desktop width, the board's core status groups (Backlog, In Progress, Done) SHALL be reachable by default — no full core group SHALL be hidden off-screen behind the board's horizontal overflow. The Cancelled group MAY collapse to a lower-priority or hidden-body state by default to preserve core-group reachability, but it SHALL NOT be removed, and its issue count SHALL remain discoverable from the default board state.

#### Scenario: Core groups are reachable at a common desktop width

- **WHEN** the board renders at a common desktop width with issues present in Backlog, In Progress, and Done
- **THEN** the Backlog, In Progress, and Done groups SHALL each be reachable without horizontally scrolling past a full core group
- **AND** no full core group SHALL be clipped behind the board's horizontal overflow by default

#### Scenario: Cancelled may collapse by default but is not removed

- **WHEN** the board renders in its default state with cancelled issues present
- **THEN** the Cancelled group MAY present in a collapsed or hidden-body state by default
- **AND** the cancelled issue count SHALL remain discoverable from the default state via a show/collapse affordance
- **AND** the Cancelled group SHALL NOT be removed from the board

### Requirement: Issue cards preserve six decision dimensions while staying compact

Each issue card SHALL render, while remaining compact, the six decision dimensions an owner needs: issue number, title, priority, status signal, workflow stage, and health signal. No dimension SHALL be dropped to achieve density; the card SHALL instead compact via a converged top row and a clamped title so all six remain inspectable. The health signal SHALL be surfaced through the status indicator and/or a per-card action affordance whenever the issue is in a non-active health state (blocked, interrupted, or drifted).

#### Scenario: A card exposes all six dimensions while staying compact

- **WHEN** a card renders for an issue that carries a priority, a workflow stage, and stage progress
- **THEN** the card SHALL expose the issue number, the title, the priority, a status signal, the workflow stage, and a health signal
- **AND** the card SHALL keep compact density via a converged single top row and a title clamped to a bounded number of lines

#### Scenario: A blocked or approval-waiting card preserves stage and health

- **WHEN** a card renders for a blocked or approval-awaiting issue
- **THEN** the status signal SHALL reflect the blocked or approval state
- **AND** the workflow stage SHALL be expressed within that status signal rather than as an independent competing pill
- **AND** the health signal SHALL remain present
- **AND** the issue number, title, and priority SHALL remain visible

#### Scenario: Cards do not expand to consume the column

- **WHEN** multiple cards render in a column
- **THEN** each card SHALL keep its title clamped to a bounded number of lines
- **AND** the card SHALL NOT expand vertically in a way that pushes sibling cards or the column out of compact density

### Requirement: Filter, search, and sort stay reachable on the first screen

The board's filter, search, and sort controls SHALL remain reachable without pushing the board content below the useful first screen. On desktop the controls SHALL occupy a single compact header row above the board so the first screen of column content remains visible. On mobile the controls SHALL remain reachable in the board header without overlapping the stage tabs or the card list.

#### Scenario: Desktop controls occupy one compact row above the board

- **WHEN** the board renders at desktop width
- **THEN** the search input, the priority filter affordance, the label filter affordance, and the single global sort control SHALL all be present in the filter bar
- **AND** the board's column content SHALL begin within the useful first screen rather than being pushed below it

#### Scenario: Mobile controls stay reachable without overlap

- **WHEN** the board renders at mobile width
- **THEN** a search input and a filter affordance SHALL be reachable in the board header
- **AND** the controls SHALL NOT overlap the stage tabs or the card list
- **AND** the filter affordance SHALL expand into a panel rather than consuming horizontal space beside the list

### Requirement: Mobile board navigation does not overlap

On mobile, the user SHALL be able to switch board groups, open an issue, and use the primary board action with competing navigation and action surfaces reduced so they do not overlap and do not force excessive horizontal scanning. Mobile support is scoped to basic board use, not a complete mobile-first workflow.

#### Scenario: Switching board groups via stage tabs

- **WHEN** a user views the mobile board
- **THEN** stage tabs SHALL be present to switch between status groups
- **AND** the stage tabs SHALL NOT overlap the filter bar or the card list

#### Scenario: Opening an issue from the mobile board

- **WHEN** a user taps a card on the mobile board
- **THEN** the corresponding issue SHALL open
- **AND** the card's tap target SHALL NOT be obstructed by an overlapping surface

#### Scenario: Primary board action is reachable on mobile

- **WHEN** a card exposes the primary board action (such as rerun or resume) on the mobile board
- **THEN** the action SHALL be reachable on the card
- **AND** the action SHALL NOT overlap another card surface or the stage tabs
