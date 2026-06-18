## MODIFIED Requirements

### Requirement: REQ-WUI-209-001 Homepage is a decision-first work entry

The Issues page SHALL surface user-actionable work before the Kanban board by rendering a compact `Needs attention` summary above the board when actionable items exist. The summary SHALL derive from existing issue and agent data and use user-facing decision labels rather than raw internal state names. This behavior now lives on the Issues route that hosts the Kanban board, not on the default landing page (the Dashboard).

#### Scenario: Homepage surfaces actionable issues first
- **WHEN** the Issues page contains issues awaiting approval, interrupted issues, blocked issues, integrate failures, or done issues that are not merged
- **THEN** the page shows a `Needs attention` summary above the board
- **AND** each summary item uses user-action language such as `Approval needed`, `Integration failed`, `Interrupted`, `Needs action`, or `Not merged`
- **AND** optional detail text may explain the secondary reason without replacing the primary action label

#### Scenario: Attention summary does not replace board navigation
- **WHEN** a user selects an item in the `Needs attention` summary
- **THEN** the user can open the relevant issue directly
- **AND** the Kanban board remains available below as the main browsing surface

### Requirement: REQ-WUI-209-002 Desktop and mobile board layouts preserve work visibility

The Issues page SHALL render the Kanban board as horizontally visible stage columns side by side at `md+` widths while preserving the existing shared filter and sort behavior. On mobile, the page SHALL preserve the single-stage board model and keep issue content visible without forcing the user to scroll past a full control matrix first. The board-hosting surface is now the Issues route rather than the default landing page.

#### Scenario: Desktop board renders side by side columns
- **WHEN** a user views the Issues page at `md+` widths
- **THEN** the stage columns render side by side in a horizontal board container
- **AND** the board does not stack all stage columns vertically
- **AND** existing board filtering and shared sort behavior still apply across the visible columns

#### Scenario: Mobile still prioritizes issue content
- **WHEN** a user views the Issues page on mobile
- **THEN** the page keeps the single-stage board model
- **AND** filter controls are compact enough that issue content is visible in the first screen

#### Scenario: Done history remains available but de-emphasized
- **WHEN** the Issues page renders the Done column
- **THEN** done/history work remains available on the board
- **AND** its presentation is visually de-emphasized relative to active and attention work

### Requirement: REQ-WUI-209-003 Homepage label filtering reaches all labels

The Issues page SHALL preserve the #198 URL-backed search, priority, label, and sort model while making all project labels reachable from the label filter UI. The filter surface SHALL remain compact and SHALL NOT limit reachable labels to the first eight returned labels. This filter/sort behavior applies on the Issues route that hosts the Kanban board.

#### Scenario: Label beyond the first eight is selectable
- **WHEN** the project contains more than eight labels
- **AND** a user wants to filter by a label that is not in the first eight visible labels
- **THEN** the Issues page provides a way to discover and select that label
- **AND** the board updates using the same label-filter semantics as other labels

#### Scenario: Board state remains URL-backed
- **WHEN** a user applies search, priority, label, or sort controls on the Issues page
- **THEN** the board state continues to be reflected in and restored from the URL

### Requirement: REQ-WUI-209-004 Homepage regressions are covered by tests

The Issues page SHALL include regression coverage for the decision-first summary, desktop multi-column visibility, and label reachability beyond the first eight labels. The tests SHALL target the Issues route that hosts the Kanban board.

#### Scenario: Desktop layout regression is caught
- **WHEN** the Issues page component tests run
- **THEN** they fail if the desktop board no longer renders with a horizontal multi-column contract at `md+` widths

#### Scenario: Hidden label regression is caught
- **WHEN** the Issues page component tests run against label data sets with more than eight labels
- **THEN** they verify a label beyond the first eight is discoverable/selectable
- **AND** they verify filtering by that label updates the board content or displayed counts

#### Scenario: Attention summary wording is covered
- **WHEN** the Issues page component tests run against representative actionable issue data
- **THEN** they verify the `Needs attention` summary renders user-action wording rather than only raw internal status names

## REMOVED Requirements

### Requirement: 无项目时显示空状态引导

**Reason:** The project empty-state behavior is relocated from the old HomePage (which hosted the Kanban) to the new Dashboard landing page introduced by the `dashboard-shell` capability. The HomePage/Kanban surface no longer owns or renders this empty-state.

**Migration:** The full empty-state behavior on the Dashboard landing page is defined in the `dashboard-shell` capability spec (`Dashboard shows project empty-state`). The Kanban surface, now reachable at the `Issues` route, SHALL NOT render the project empty-state.

## ADDED Requirements

### Requirement: Primary navigation leads with Dashboard and Issues

The Web App-Shell primary navigation SHALL include `Dashboard` and `Issues` as the first two entries, where `Dashboard` targets the default landing page and `Issues` targets the relocated Kanban board. The full primary navigation order SHALL be: `Dashboard`, `Issues`, `Activity`, `Epics`, `Logs`, `Settings`, `Archived`. The `Issues` entry SHALL replace the prior `Board`/`Home` entry that pointed at the Kanban-as-home.

#### Scenario: Sidebar contains Dashboard and Issues entries

- **WHEN** a user views the desktop sidebar (`AppSidebar`)
- **THEN** the navigation SHALL include a `Dashboard` entry and an `Issues` entry
- **AND** the `Dashboard` entry SHALL precede the `Issues` entry
- **AND** no `Board` or `Home` entry pointing at the Kanban-as-home SHALL remain

#### Scenario: Issues entry navigates to the Kanban board

- **WHEN** a user activates the `Issues` navigation entry
- **THEN** the application navigates to the route that hosts the Kanban board
- **AND** the Kanban board renders with its existing filter, search, and sort behavior

#### Scenario: Dashboard entry navigates to the default landing

- **WHEN** a user activates the `Dashboard` navigation entry
- **THEN** the application navigates to the Dashboard page
- **AND** the Dashboard renders as the default landing surface

### Requirement: Desktop and mobile navigation stay synchronized

The desktop sidebar (`AppSidebar`) and the mobile bottom navigation (`MobileBottomNav`) SHALL expose the same primary navigation destinations and SHALL stay synchronized. Both surfaces SHALL provide access to `Dashboard` and `Issues` alongside the rest of the canonical navigation set.

#### Scenario: Mobile bottom nav includes Dashboard and Issues

- **WHEN** a user views the mobile bottom navigation at mobile widths
- **THEN** the bottom navigation SHALL provide access to the `Dashboard` and `Issues` destinations
- **AND** activating either destination SHALL navigate to the same route as the corresponding desktop sidebar entry

#### Scenario: Navigation destinations match across surfaces

- **WHEN** the desktop sidebar and mobile bottom navigation are both rendered
- **THEN** the primary navigation destinations SHALL be consistent across both surfaces
- **AND** a destination reachable on one surface SHALL be reachable on the other

### Requirement: Kanban behavior is preserved on the Issues route

The Kanban board, relocated from the default landing to the `Issues` route, SHALL preserve its existing behavior without regression. Filtering, search, sort, and URL query state (`?priorities=...&labels=...`) SHALL continue to work identically on the relocated route, and existing Kanban tests SHALL continue to pass.

#### Scenario: Kanban URL query behavior is preserved

- **WHEN** a user opens the Issues route with board query parameters such as `?priorities=...&labels=...`
- **THEN** the Kanban board SHALL restore its filtered/sorted state from the URL
- **AND** this behavior SHALL not regress relative to the previous home-route behavior

#### Scenario: Kanban tests pass on the relocated route

- **WHEN** the existing Kanban component and integration tests run against the Issues route
- **THEN** the tests SHALL pass without modification to Kanban behavior
- **AND** no Kanban filtering, search, or sort capability SHALL be removed
