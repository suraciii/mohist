## MODIFIED Requirements

### Requirement: Dashboard provides four zone mount-point slots

The Dashboard page SHALL expose exactly four zone mount-point slots with stable identities: `Attention`, `Pulse`, `Productivity`, and `Digest`. The slots SHALL serve as the composition contract that downstream issues fill. The `Digest` slot SHALL mount the `dashboard-recent-digest` zone content. The `Attention`, `Pulse`, and `Productivity` slots SHALL render as empty placeholders until their respective downstream zone issues land.

#### Scenario: Four zone slots render with stable identities

- **WHEN** the Dashboard page renders
- **THEN** the page SHALL contain four zone mount-point slots named `Attention`, `Pulse`, `Productivity`, and `Digest`
- **AND** the `Digest` slot SHALL render the `dashboard-recent-digest` zone content
- **AND** the `Attention`, `Pulse`, and `Productivity` slots SHALL render as empty placeholders

#### Scenario: Zone slot identities are stable

- **WHEN** a downstream zone view targets a Dashboard slot
- **THEN** the slot identities `Attention`, `Pulse`, `Productivity`, and `Digest` SHALL be stable across renders
- **AND** a slot SHALL be addressable by its identity as a mount point for zone content

#### Scenario: Digest slot mounts recent-digest zone content

- **WHEN** the Dashboard page renders for a project that has at least one project
- **THEN** the `Digest` slot SHALL render the `dashboard-recent-digest` zone content in place of the empty placeholder
- **AND** the `Attention`, `Pulse`, and `Productivity` slots SHALL NOT render zone content
