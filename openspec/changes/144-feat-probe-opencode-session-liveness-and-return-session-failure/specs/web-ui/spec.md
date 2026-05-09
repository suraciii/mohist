## MODIFIED Requirements

### Requirement: REQ-WUI-001 Web UI shows simplified current session state

Web UI SHALL render only the simplified current opencode session call states for this feature: Running, Checking session, Session failed, and No active session.

#### Scenario: Running session displayed
- **WHEN** an issue has a current session with status `running`
- **THEN** Web UI SHALL display `Running`
- **AND** last response/data time MAY be displayed when available

#### Scenario: Probing session displayed
- **WHEN** an issue has a current session with status `probing`
- **THEN** Web UI SHALL display `Checking session`
- **AND** it SHALL display probe timing when available

#### Scenario: Failed session displayed
- **WHEN** an issue has a current session with status `failed`
- **THEN** Web UI SHALL display `Session failed`
- **AND** it SHALL display `failureReason` when available

#### Scenario: No active session displayed
- **WHEN** an issue has no current running, probing, or failed session call relevant to the current task
- **THEN** Web UI SHALL display `No active session` where the current session state is shown

#### Scenario: Complex health labels not shown
- **WHEN** Web UI renders session liveness state
- **THEN** it SHALL NOT show healthy, quiet, stale, hung-suspected, or recoverable as user-facing states for this feature
