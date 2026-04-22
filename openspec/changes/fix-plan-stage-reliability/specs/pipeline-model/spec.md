## ADDED Requirements

### Requirement: Server startup recovery for orphaned issues
The system SHALL detect and recover orphaned active issues during server startup. An issue is orphaned when its status is `active` but stage is not `draft` and no agent is running for it.

#### Scenario: Recover orphaned issues on startup
- **WHEN** the server starts
- **AND** there are issues with status `active` and stage not `draft`
- **THEN** the system marks each orphaned issue as `blocked`
- **AND** rolls back each orphaned issue's stage to `draft`
- **AND** clears any pending approval state
- **AND** logs the recovery action with issue numbers

#### Scenario: No orphaned issues on startup
- **WHEN** the server starts
- **AND** there are no issues with status `active` and stage not `draft`
- **THEN** no recovery action is taken
- **AND** the server starts normally
