# OpenSpec Capability: workflow-config

### Requirement: workflow profiles define runnable workflow behavior

Workflow profiles SHALL define runnable stages, tasks, checks, approvals, repairs, and variables through workflow definitions managed by the profile system.

#### Scenario: Default workflow definition
- **WHEN** an issue has no project or issue workflow profile override
- **THEN** the system uses the bundled default workflow definition
- **AND** the default definition matches `mohist-default.workflow.yaml`

#### Scenario: Project or issue workflow profile override
- **WHEN** a project or issue profile selects or overrides a workflow definition
- **THEN** workflow loading resolves the effective definition through the profile manager
- **AND** WorkflowRun execution uses that resolved definition

#### Scenario: Invalid workflow definition
- **WHEN** a selected workflow definition cannot be parsed or validated
- **THEN** workflow loading reports a clear configuration error
- **AND** it does not start a WorkflowRun from invalid definition data

### Requirement: workflow validation distinguishes lifecycle state from runnable stages (REQ-001)

Workflow configuration SHALL validate runnable stages independently from the issue lifecycle start state.

#### Scenario: Backlog remains lifecycle start state
- **WHEN** an issue is created under the default workflow
- **THEN** the issue still starts in `backlog`
- **AND** `backlog` is not required to appear in the runnable workflow stage list

#### Scenario: Deprecated workflow stage values are rejected or ignored by validation rules
- **WHEN** workflow loading or validation evaluates stage names
- **THEN** it does not require `draft` or `explore` for a valid workflow
- **AND** no built-in workflow configuration advertises `explore` as runnable

### Requirement: Check full verification policy compatibility

Workflow configuration SHALL resolve the Check full verification command from `healthGates.check` when present and from legacy `checks.buildTest` when `healthGates.check` is absent.

#### Scenario: healthGates.check configures Check verification

- **WHEN** workflow configuration defines `healthGates.check.command` or related policy fields
- **THEN** Check full verification SHALL use the resolved `healthGates.check` policy

#### Scenario: checks.buildTest configures Check verification by compatibility

- **WHEN** workflow configuration defines `checks.buildTest`
- **AND** `healthGates.check` is absent
- **THEN** Check full verification SHALL use the compatible `checks.buildTest` command, timeout, and retry policy fields

#### Scenario: Disabled Check verification cannot satisfy approval evidence

- **WHEN** `healthGates.check.enabled` is `false`
- **THEN** the system SHALL record that Check verification was disabled by policy
- **AND** disabled verification SHALL NOT count as passing full verification evidence for Check approval
