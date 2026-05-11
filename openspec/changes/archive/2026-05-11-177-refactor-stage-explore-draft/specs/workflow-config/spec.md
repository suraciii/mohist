## MODIFIED Requirements

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
