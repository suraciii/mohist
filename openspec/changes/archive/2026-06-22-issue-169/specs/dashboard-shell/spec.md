## MODIFIED Requirements

### Requirement: Dashboard provides four zone mount-point slots

The Dashboard page SHALL expose exactly four zone mount-point slots with stable identities: `Attention`, `Pulse`, `Productivity`, and `Digest`. Each slot SHALL serve as the composition contract that the corresponding downstream zone issue fills. The `Productivity` slot SHALL render Productivity zone content (owned by the `dashboard-productivity` capability). The `Attention`, `Pulse`, and `Digest` slots SHALL remain empty placeholders with no zone content until their downstream zone issues land.

#### Scenario: Productivity slot renders zone content while other slots stay empty

- **WHEN** the Dashboard page renders
- **THEN** the page SHALL contain four zone mount-point slots named `Attention`, `Pulse`, `Productivity`, and `Digest`
- **AND** the `Productivity` slot SHALL render the Productivity zone content
- **AND** the `Attention`, `Pulse`, and `Digest` slots SHALL render as empty placeholders with no zone content

#### Scenario: Zone slot identities are stable

- **WHEN** a downstream zone view targets a Dashboard slot
- **THEN** the slot identities `Attention`, `Pulse`, `Productivity`, and `Digest` SHALL be stable across renders
- **AND** a slot SHALL be addressable by its identity as a mount point for zone content

#### Scenario: Unfilled zone slots defer content to downstream issues

- **WHEN** the Dashboard page renders for any project state
- **THEN** the `Attention`, `Pulse`, and `Digest` slots SHALL NOT render their respective zone content
- **AND** the zone content for those three slots SHALL remain the responsibility of their downstream issues
