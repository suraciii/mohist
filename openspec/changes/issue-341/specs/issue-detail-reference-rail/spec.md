# issue-detail-reference-rail Specification

## Requirements

### Requirement: Metadata and Low-Frequency Configuration Only

The reference rail SHALL hold only issue metadata, low-frequency configuration, and non-runtime issue actions — details, model, workflow-profile control, prerequisites, the non-runtime IssueActionsCard (Mark ready / Close / Ask Agent / archived note / draft readiness), and the low-frequency drift and convergence panels. It SHALL NOT contain the runtime decision/action surface, the workflow progress/outputs, or the changes/diff, commits, description, or comments blocks.

#### Scenario: Rail holds metadata and configuration

- **WHEN** the reference rail renders
- **THEN** the details metadata, the model configuration, the workflow-profile control, and any prerequisites appear in the rail

#### Scenario: Primary content is excluded from the rail

- **WHEN** the reference rail renders
- **THEN** the runtime decision/action surface is not in the rail
- **AND** the workflow stage progress, outputs, changes/diff, commits, description, and comments are not in the rail

#### Scenario: Non-runtime issue actions live in the rail

- **WHEN** the reference rail renders
- **THEN** the IssueActionsCard (Mark ready, Close, Ask Agent) appears in the rail
- **AND** it does not duplicate the seven runtime actions anchored in the status-header tier

### Requirement: Desktop Right Column

On desktop the reference rail SHALL render as a right column beside the reading flow, narrower than the reading flow.

#### Scenario: Desktop two-column layout

- **WHEN** the detail page renders on a desktop viewport
- **THEN** the reference rail appears as a column on the right of the reading flow
- **AND** the rail column is narrower than the reading-flow column

### Requirement: Narrow-Screen Collapsed Sections

On narrow screens the reference rail SHALL render as collapsed sections rather than as a right column, so peripheral configuration does not crowd the reading flow.

#### Scenario: Narrow viewport collapses the rail

- **WHEN** the detail page renders on a narrow viewport
- **THEN** the reference rail renders as stacked collapsed sections
- **AND** it does not occupy a right column beside the reading flow

### Requirement: Low-Frequency Items Collapsed by Default

Low-frequency items — drift and convergence — SHALL be collapsed by default and SHALL expand on demand, so they do not draw attention unless requested.

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
