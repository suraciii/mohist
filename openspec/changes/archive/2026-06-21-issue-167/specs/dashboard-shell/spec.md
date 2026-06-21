## MODIFIED Requirements

### Requirement: Dashboard provides four zone mount-point slots

The Dashboard page SHALL expose exactly four zone mount-point slots with stable identities: `Attention`, `Pulse`, `Productivity`, and `Digest`. The slots SHALL serve as the composition contract that downstream issues fill. The `Attention` slot SHALL host the Attention Hero view (defined by the `dashboard-attention-hero` capability); the `Pulse`, `Productivity`, and `Digest` slots SHALL render as empty placeholders with no zone content until their respective downstream issues implement them.

#### Scenario: Attention slot hosts the Attention Hero

- **WHEN** the Dashboard page renders for a project that has at least one project
- **THEN** the `Attention` slot SHALL render the Attention Hero view
- **AND** the `Attention` slot SHALL NOT render an empty placeholder

#### Scenario: Non-Attention zone slots render as empty placeholders

- **WHEN** the Dashboard page renders
- **THEN** the `Pulse`, `Productivity`, and `Digest` slots SHALL each render as an empty placeholder
- **AND** each of those placeholders SHALL be empty

#### Scenario: Zone slot identities are stable

- **WHEN** a downstream zone view targets a Dashboard slot
- **THEN** the slot identities `Attention`, `Pulse`, `Productivity`, and `Digest` SHALL be stable across renders
- **AND** a slot SHALL be addressable by its identity as a mount point for zone content

#### Scenario: Non-Attention zone content remains downstream-owned

- **WHEN** the Dashboard page renders for any project state
- **THEN** none of the `Pulse`, `Productivity`, or `Digest` slots SHALL render their zone content
- **AND** their zone content SHALL remain the responsibility of downstream issues
