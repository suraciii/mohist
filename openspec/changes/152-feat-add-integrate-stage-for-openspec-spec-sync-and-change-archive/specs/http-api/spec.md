## MODIFIED Requirements

### Requirement: integrate approval and recovery
The API SHALL route Check approval and pipeline recovery through the Integrate stage.

#### Scenario: Approve Check
- **WHEN** a user approves an issue awaiting Check approval
- **THEN** the API marks the approval approved
- **AND** transitions the issue to `integrate`
- **AND** resumes the pipeline through the Integrate runner

#### Scenario: Direct merge cannot bypass Integrate
- **WHEN** a caller requests a direct merge path
- **THEN** the API either uses the same Integrate contract or rejects the request with a clear bypass-prevention error
- **AND** Done is not reached without successful integration evidence

#### Scenario: Recover active Integrate issue
- **WHEN** the server recovers an active issue in `integrate`
- **THEN** the issue is reported as recoverable
- **AND** resume continues Integrate rather than treating the issue as Done
