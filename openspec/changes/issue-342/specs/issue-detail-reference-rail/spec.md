# issue-detail-reference-rail Specification

## Requirements

### Requirement: Metadata and Low-Frequency Configuration Only

The reference rail SHALL hold only issue metadata, low-frequency configuration, and non-runtime issue actions — details, model, workflow-profile control, prerequisites, the non-runtime IssueActionsCard (Mark ready / Close / Ask Agent / archived note / draft readiness), and the low-frequency drift and convergence panels. It SHALL NOT contain the runtime decision/action surface, the workflow progress/outputs, the changes/diff, commits, description, or comments blocks.

#### Scenario: Rail holds metadata and configuration

- **WHEN** the reference rail renders on any viewport
- **THEN** the details metadata, the model configuration, the workflow-profile control, and any prerequisites appear in the rail

#### Scenario: Primary content is excluded from the rail

- **WHEN** the reference rail renders on any viewport
- **THEN** the runtime decision/action surface is not in the rail
- **AND** the workflow stage progress, outputs, changes/diff, commits, description, and comments are not in the rail

### Requirement: Desktop Right Column Restored at `lg` and Wider

On tablet and desktop viewports (`lg`/1024px and wider) the reference rail SHALL render as a right column beside the reading flow, narrower than the reading flow, and SHALL NOT render any mobile-only element (floating action bar, confirmation drawer). The two-column desktop layout SHALL be fully restored.

#### Scenario: Desktop two-column layout

- **WHEN** the detail page renders on a viewport at or above `lg` (1024px)
- **THEN** the reference rail appears as a column on the right of the reading flow
- **AND** the rail column is narrower than the reading-flow column
- **AND** no floating action bar or bottom confirmation drawer renders

### Requirement: Narrow-Screen Collapse Into Stacked Expandable Sections After the Reading Flow

On narrow viewports (below `lg`/1024px) the reference rail SHALL render as stacked, expandable collapsible sections placed after the reading flow rather than as a right column beside it, so peripheral configuration does not crowd the reading flow. The collapse ordering SHALL follow the reading flow: rail sections appear beneath the last reading-flow item.

#### Scenario: Narrow viewport collapses the rail into stacked sections

- **WHEN** the detail page renders on a narrow viewport
- **THEN** the reference rail renders as stacked collapsible sections
- **AND** it does not occupy a right column beside the reading flow

#### Scenario: Rail sections follow the reading flow on a narrow viewport

- **WHEN** the detail page renders on a narrow viewport
- **THEN** every reference-rail section is positioned after the last item of the reading flow in document order

### Requirement: Low-Frequency Items Collapsed by Default

Low-frequency items — drift and convergence — SHALL be collapsed by default on every viewport and SHALL expand only on a deliberate user action, so they do not draw attention unless requested.

#### Scenario: Drift panel is collapsed initially

- **WHEN** the reference rail renders for an issue with base drift detected
- **THEN** the drift panel is collapsed by default
- **AND** expanding it is a deliberate user action

#### Scenario: Convergence panel is collapsed initially

- **WHEN** the reference rail renders for an issue with convergence items
- **THEN** the convergence panel is collapsed by default
- **AND** expanding it is a deliberate user action

### Requirement: Lightest Visual Weight

The reference rail SHALL carry the lightest visual weight of the three detail-page tiers, lighter than both the status headline and the reading flow, so peripheral configuration recedes behind the primary content.

#### Scenario: Rail is the lightest tier

- **WHEN** the detail page renders all three tiers
- **THEN** the reference rail is visually lighter than the reading flow
- **AND** the reading flow is visually lighter than the status headline
