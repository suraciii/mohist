### Requirement: Desktop board layout contains its own horizontal scroll

The issue kanban board's four columns (Backlog / In Progress / Done / Cancelled) SHALL be fully visible without clipping at a 1440px desktop viewport, and the page SHALL NOT render a whole-page horizontal scrollbar. When the columns' total width exceeds the available content area, horizontal scrolling SHALL be confined to the board region and SHALL NOT propagate to the app shell. The left navigation SHALL remain fixed in place and SHALL NOT be displaced by the board's horizontal scroll.

#### Scenario: All four columns are visible at desktop width

- **WHEN** the board renders at a 1440px desktop viewport with all four columns present
- **THEN** the Backlog, In Progress, Done, and Cancelled columns SHALL all be visible without clipping
- **AND** the page SHALL NOT render a whole-page horizontal scrollbar

#### Scenario: Board region owns overflow instead of the page

- **WHEN** the board's total column width exceeds the available content area
- **THEN** horizontal scrolling SHALL be contained within the board region
- **AND** the scroll SHALL NOT propagate to the document body or app shell

#### Scenario: Left navigation stays fixed during board scroll

- **WHEN** a user scrolls the board region horizontally
- **THEN** the left navigation SHALL remain fixed in place
- **AND** the app shell layout SHALL NOT be shifted horizontally

### Requirement: Card top row is density-converged

Each card's default top row SHALL render only the issue number, the priority indicator, and at most one dominant status. The full workflow-profile string SHALL NOT be rendered as visible text in the default top row; it SHALL be exposed only as a hover hint via a `title` attribute (or equivalent accessible hover affordance) so the value remains inspectable without occupying default visual density. Stage information SHALL NOT render as an independent second pill alongside a status pill; when a card carries a Running / Approval / Blocked / Drift (etc.) status pill, the stage information SHALL fold into that status expression. Stage progress numbers SHALL render as part of the stage label rather than as a standalone unit.

#### Scenario: Default top row keeps only essential elements

- **WHEN** a card renders in its default (non-hovered) state
- **THEN** the visible top row SHALL contain the issue number, the priority indicator, and at most one status pill
- **AND** the full workflow-profile string SHALL NOT be visible as text in the top row

#### Scenario: Workflow profile is available on hover

- **WHEN** a user hovers over a card's top-row hover affordance (e.g. the issue number with its `title`)
- **THEN** the workflow-profile value SHALL be available via a hover hint (`title` attribute)
- **AND** the value SHALL remain inspectable despite not being rendered as default text

#### Scenario: Stage folds into a status pill instead of stacking

- **WHEN** a card renders with a Running / Approval / Blocked / Drift status pill
- **THEN** the stage information SHALL be expressed within that status pill
- **AND** no independent second stage pill SHALL be rendered alongside the status pill

#### Scenario: Progress renders as part of the stage label

- **WHEN** a card renders stage progress numbers
- **THEN** the progress SHALL appear as part of the stage label
- **AND** SHALL NOT render as a standalone unit

### Requirement: Per-card text and pills meet WCAG AA contrast

Issue numbers, timestamps, and other per-card auxiliary text SHALL reach a contrast ratio of at least 4.5:1 against their background (WCAG AA). Status pill and priority pill background/text combinations SHALL likewise reach at least 4.5:1 contrast. This is an accessibility hard requirement, not a stylistic preference.

#### Scenario: Auxiliary text meets AA contrast

- **WHEN** a card renders the issue number and timestamp text
- **THEN** the text SHALL meet at least 4.5:1 contrast against its background

#### Scenario: Status and priority pills meet AA contrast

- **WHEN** a status pill or priority pill renders
- **THEN** its background/text color combination SHALL meet at least 4.5:1 contrast

### Requirement: The board exposes a single global sort control

The board SHALL expose exactly one global sort control, located in the top filter bar. Per-column-header sort button groups SHALL NOT be rendered, eliminating redundant sort controls bound to the same sort state.

#### Scenario: Exactly one sort control lives in the top filter bar

- **WHEN** the board renders
- **THEN** exactly one sort control SHALL be present in the top filter bar
- **AND** no per-column-header sort button group SHALL be rendered

#### Scenario: A single sort state drives all columns

- **WHEN** a user changes the sort via the top filter bar control
- **THEN** all columns SHALL be sorted according to that single sort state

### Requirement: The card color strip is driven by priority

The card's left color strip SHALL take its color from the issue priority. The priority-to-color mapping SHALL be deterministic and distinct across priorities (e.g. P0 red / P1 orange / P2 yellow / P3 green / P4 gray). The strip SHALL NOT derive its color from type labels, so that an issue lacking type labels still presents a meaningful, non-default-gray color.

#### Scenario: Different priorities present distinguishable hues

- **WHEN** cards of differing priorities render on the board
- **THEN** each card's left color strip SHALL reflect its priority via the priority-to-color mapping
- **AND** cards of differing priorities SHALL present visually distinguishable hues

#### Scenario: Color is stable without type labels

- **WHEN** an issue has no type labels
- **THEN** the card's left color strip SHALL still render a color determined by its priority
- **AND** SHALL NOT fall back to a default gray due to missing labels

### Requirement: Existing board behaviors remain functional across desktop and mobile layouts

The visual/interaction convergence SHALL NOT regress the board's existing behaviors. Filtering, sorting, Done-column collapse, Show/Hide cancelled, Archive and Archive-all-done, and the Needs-Attention banner SHALL continue to function correctly in both desktop and mobile layouts.

#### Scenario: Core interactions work on desktop layout

- **WHEN** a user exercises filtering, sorting, Done-column collapse, Show/Hide cancelled, Archive / Archive-all-done, and the Needs-Attention banner on the desktop layout
- **THEN** each interaction SHALL function correctly
- **AND** SHALL NOT be broken by the layout/density convergence

#### Scenario: Core interactions work on mobile layout

- **WHEN** a user exercises filtering, sorting, Done-column collapse, Show/Hide cancelled, Archive / Archive-all-done, and the Needs-Attention banner on the mobile layout
- **THEN** each interaction SHALL function correctly
- **AND** SHALL NOT be broken by the layout/density convergence
