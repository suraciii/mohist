## MODIFIED Requirements

### Requirement: REQ-CLI-001 CLI shows simplified current session state

CLI issue/status output SHALL render the same simplified current opencode session call states as Web UI.

#### Scenario: CLI shows running
- **WHEN** an issue has a current session with status `running`
- **THEN** CLI output SHALL show `Running`

#### Scenario: CLI shows checking session
- **WHEN** an issue has a current session with status `probing`
- **THEN** CLI output SHALL show `Checking session`
- **AND** it SHOULD include probe timing when available

#### Scenario: CLI shows session failed
- **WHEN** an issue has a current session with status `failed`
- **THEN** CLI output SHALL show `Session failed`
- **AND** it SHOULD include `failureReason` when available

#### Scenario: CLI shows no active session
- **WHEN** an issue has no current active session call
- **THEN** CLI output SHALL show `No active session` where current session state is displayed
