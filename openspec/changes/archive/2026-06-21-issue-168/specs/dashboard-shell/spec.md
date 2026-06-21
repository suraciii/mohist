## MODIFIED Requirements

### Requirement: Dashboard provides four zone mount-point slots

The Dashboard page SHALL expose exactly four zone mount-point slots with stable identities: `Attention`, `Pulse`, `Productivity`, and `Digest`. Each slot SHALL serve as the composition contract for a zone-specific capability. A slot whose zone capability has been implemented SHALL render that capability's content; a slot whose zone capability has not yet been implemented SHALL render as an empty placeholder. The `Pulse` slot's content SHALL be governed by the `dashboard-pulse` capability.

#### Scenario: Zone slot identities are stable

- **WHEN** a downstream zone view targets a Dashboard slot
- **THEN** the slot identities `Attention`, `Pulse`, `Productivity`, and `Digest` SHALL be stable across renders
- **AND** a slot SHALL be addressable by its identity as a mount point for zone content

#### Scenario: Unimplemented zone slots render empty

- **WHEN** the Dashboard page renders and a zone slot has no implemented zone capability
- **THEN** that slot SHALL render as an empty placeholder
- **AND** the slot identity SHALL remain stable so a future zone capability can fill it

#### Scenario: Implemented zone slot renders its capability content

- **WHEN** the Dashboard page renders and a zone slot is governed by an implemented zone capability
- **THEN** that slot SHALL render the content defined by that capability
- **AND** other slots SHALL remain independently empty or filled according to their own capabilities

#### Scenario: Pulse slot is governed by dashboard-pulse

- **WHEN** the Dashboard page renders the `Pulse` slot
- **THEN** the slot SHALL render the content defined by the `dashboard-pulse` capability
- **AND** the slot identity and mount-point contract SHALL remain unchanged from the original skeleton
