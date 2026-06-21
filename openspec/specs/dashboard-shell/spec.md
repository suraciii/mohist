# OpenSpec Capability: dashboard-shell

### Requirement: Dashboard is the default landing page

The Web App-Shell SHALL resolve the project root route (`/` and `/:projectName`) to the Dashboard page. The Dashboard SHALL be the default surface a user lands on when entering the application, replacing the previous Kanban-as-home behavior. The Kanban board SHALL no longer be the default landing.

#### Scenario: Root path lands on Dashboard

- **WHEN** a user navigates to the application root or a project root path
- **THEN** the Dashboard page SHALL render as the default landing
- **AND** the Kanban board SHALL NOT render as the default landing

#### Scenario: Dashboard renders as an empty skeleton

- **WHEN** the Dashboard page renders for a project that has at least one project
- **THEN** the page SHALL render a page container with zone mount-point placeholders
- **AND** the page SHALL NOT render the Kanban board

#### Scenario: Default landing is independent of Kanban navigation

- **WHEN** a user opens the application without an explicit deep link
- **THEN** the user arrives at the Dashboard rather than the Kanban board
- **AND** the Kanban board remains reachable from primary navigation under the `Issues` entry

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

### Requirement: Dashboard shows project empty-state

The Dashboard page SHALL own the project empty-state that previously resided on the old HomePage. When no projects exist, the Dashboard SHALL render a `No projects yet` prompt with a `Create Project` action instead of rendering zone slots or the Kanban board.

#### Scenario: No projects shows empty-state on Dashboard

- **WHEN** the Dashboard page renders and the project list is empty
- **THEN** the page SHALL display the `No projects yet` prompt
- **AND** the page SHALL display a `Create Project` action
- **AND** the page SHALL NOT render zone mount-point placeholders or the Kanban board

#### Scenario: Create Project from Dashboard empty-state

- **WHEN** a user activates the `Create Project` action from the Dashboard empty-state
- **THEN** the `CreateProjectDialog` SHALL open
- **AND** after a project is created successfully the Dashboard SHALL show the Dashboard skeleton with its zone slots

#### Scenario: Empty-state is no longer on the Kanban surface

- **WHEN** the Kanban board is opened via the `Issues` navigation entry and a project exists
- **THEN** the Kanban board SHALL render normally
- **AND** the project empty-state SHALL be owned by the Dashboard, not the Issues/Kanban surface