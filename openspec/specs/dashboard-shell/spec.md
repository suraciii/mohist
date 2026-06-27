### Requirement: Dashboard provides four zone mount-point slots

The Dashboard page SHALL expose a first-screen composition contract made of, top to bottom: (1) a full-width **factory status headline** mount-point slot at the very top; (2) a full-width **Attention Hero** slot directly below the headline; (3) three remaining equal-weight zone mount-point slots with stable identities — `Pulse`, `Productivity`, and `Digest` — rendered underneath the Hero. The `Digest` slot SHALL mount the `dashboard-recent-digest` zone content. The `Pulse` slot SHALL mount the `dashboard-pulse` zone content. The `Attention` slot SHALL mount the `AttentionHero` widget as a full-width Hero and SHALL NOT render as an equal-weight peer within the remaining zones grid. The `Productivity` slot SHALL mount the `ai-quality-metrics` zone content (the `QualityPanel`). No slot SHALL render as an empty placeholder. The headline and Hero SHALL each span the full content width, distinct from the remaining zones.

#### Scenario: Four zone slots render with stable identities

- **WHEN** the Dashboard page renders
- **THEN** the page SHALL render the factory status headline slot as the topmost element, spanning the full content width
- **AND** the page SHALL render the `Attention` slot directly below the headline as a full-width Hero mounting the `AttentionHero` widget
- **AND** the page SHALL render three remaining zone slots named `Pulse`, `Productivity`, and `Digest` beneath the Hero
- **AND** the `Digest` slot SHALL render the `dashboard-recent-digest` zone content
- **AND** the `Pulse` slot SHALL render the `dashboard-pulse` zone content
- **AND** the `Productivity` slot SHALL render the `ai-quality-metrics` zone content

#### Scenario: Zone slot identities are stable

- **WHEN** a downstream zone view targets a Dashboard slot
- **THEN** the slot identities `Attention`, `Pulse`, `Productivity`, and `Digest` SHALL be stable across renders
- **AND** a slot SHALL be addressable by its identity as a mount point for zone content

#### Scenario: Attention is a full-width Hero, not an equal-weight zone

- **WHEN** the Dashboard first screen renders
- **THEN** the `Attention` slot SHALL mount the `AttentionHero` widget full-width directly below the factory status headline
- **AND** the `Attention` slot SHALL NOT be a peer within the remaining zones grid
- **AND** the `Pulse`, `Productivity`, and `Digest` slots SHALL render beneath the Hero

#### Scenario: Digest slot mounts recent-digest zone content

- **WHEN** the Dashboard page renders for a project that has at least one project
- **THEN** the `Digest` slot SHALL render the `dashboard-recent-digest` zone content in place of the empty placeholder
- **AND** the `Pulse` slot SHALL render the `dashboard-pulse` zone content
- **AND** the `Productivity` slot SHALL render the `ai-quality-metrics` zone content

#### Scenario: Pulse slot mounts dashboard-pulse zone content

- **WHEN** the Dashboard page renders
- **THEN** the `Pulse` slot SHALL render the `dashboard-pulse` zone content in place of the empty placeholder
- **AND** the `Pulse` slot SHALL NOT render as an empty placeholder

### Requirement: QualityPanel derives its quality rates exclusively from the server-side AI quality aggregation

The `QualityPanel` mounted in the `Productivity` slot SHALL render the project-level first-time-right rate and the per-stage rework rates sourced exclusively from the server-side AI quality aggregation endpoint. The frontend SHALL NOT compute the quality rates client-side over the local full set of workflow runs. When the aggregation returns the empty (zero-sample) result for a window, the `QualityPanel` SHALL render a defined empty/zero-sample presentation for that window that is distinguishable from a genuine perfect quality score.

#### Scenario: Quality rates are sourced from the aggregation endpoint, not computed client-side

- **WHEN** the `QualityPanel` renders the first-time-right rate and the per-stage rework rates
- **THEN** the displayed rates SHALL be sourced from the AI quality aggregation endpoint
- **AND** the frontend SHALL NOT compute the rates client-side over the full set of workflow runs

#### Scenario: QualityPanel renders a defined empty presentation for a zero-sample window

- **WHEN** the AI quality aggregation returns the empty result for a window
- **THEN** the `QualityPanel` SHALL render a defined empty/zero-sample presentation for that window
- **AND** the empty presentation SHALL be distinguishable from a genuine perfect quality score
