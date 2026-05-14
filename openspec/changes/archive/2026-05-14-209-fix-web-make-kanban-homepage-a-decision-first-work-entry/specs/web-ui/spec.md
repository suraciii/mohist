# OpenSpec Capability: web-ui

## MODIFIED Requirements

### Requirement: REQ-WUI-209-001 Homepage is a decision-first work entry

The homepage SHALL surface user-actionable work before the Kanban board by rendering a compact `Needs attention` summary above the board when actionable items exist. The summary SHALL derive from existing issue and agent data and use user-facing decision labels rather than raw internal state names.

#### Scenario: Homepage surfaces actionable issues first
- **WHEN** the homepage contains issues awaiting approval, interrupted issues, blocked issues, integrate failures, or done issues that are not merged
- **THEN** the page shows a `Needs attention` summary above the board
- **AND** each summary item uses user-action language such as `Approval needed`, `Integration failed`, `Interrupted`, `Needs action`, or `Not merged`
- **AND** optional detail text may explain the secondary reason without replacing the primary action label

#### Scenario: Attention summary does not replace board navigation
- **WHEN** a user selects an item in the `Needs attention` summary
- **THEN** the user can open the relevant issue directly
- **AND** the Kanban board remains available below as the main browsing surface

### Requirement: REQ-WUI-209-002 Desktop and mobile board layouts preserve work visibility

The homepage SHALL render the Kanban board as horizontally visible stage columns side by side at `md+` widths while preserving the existing shared filter and sort behavior. On mobile, the page SHALL preserve the single-stage board model and keep issue content visible without forcing the user to scroll past a full control matrix first.

#### Scenario: Desktop board renders side by side columns
- **WHEN** a user views the homepage at `md+` widths
- **THEN** the stage columns render side by side in a horizontal board container
- **AND** the board does not stack all stage columns vertically
- **AND** existing board filtering and shared sort behavior still apply across the visible columns

#### Scenario: Mobile still prioritizes issue content
- **WHEN** a user views the homepage on mobile
- **THEN** the page keeps the single-stage board model
- **AND** filter controls are compact enough that issue content is visible in the first screen

#### Scenario: Done history remains available but de-emphasized
- **WHEN** the homepage renders the Done column
- **THEN** done/history work remains available on the board
- **AND** its presentation is visually de-emphasized relative to active and attention work

### Requirement: REQ-WUI-209-003 Homepage label filtering reaches all labels

The homepage SHALL preserve the #198 URL-backed search, priority, label, and sort model while making all project labels reachable from the label filter UI. The filter surface SHALL remain compact and SHALL NOT limit reachable labels to the first eight returned labels.

#### Scenario: Label beyond the first eight is selectable
- **WHEN** the project contains more than eight labels
- **AND** a user wants to filter by a label that is not in the first eight visible labels
- **THEN** the homepage provides a way to discover and select that label
- **AND** the board updates using the same label-filter semantics as other labels

#### Scenario: Board state remains URL-backed
- **WHEN** a user applies search, priority, label, or sort controls on the homepage
- **THEN** the board state continues to be reflected in and restored from the URL

### Requirement: REQ-WUI-209-004 Homepage regressions are covered by tests

The homepage SHALL include regression coverage for the decision-first summary, desktop multi-column visibility, and label reachability beyond the first eight labels.

#### Scenario: Desktop layout regression is caught
- **WHEN** the homepage component tests run
- **THEN** they fail if the desktop board no longer renders with a horizontal multi-column contract at `md+` widths

#### Scenario: Hidden label regression is caught
- **WHEN** the homepage component tests run against label data sets with more than eight labels
- **THEN** they verify a label beyond the first eight is discoverable/selectable
- **AND** they verify filtering by that label updates the board content or displayed counts

#### Scenario: Attention summary wording is covered
- **WHEN** the homepage component tests run against representative actionable issue data
- **THEN** they verify the `Needs attention` summary renders user-action wording rather than only raw internal status names
