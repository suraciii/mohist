## ADDED Requirements

### Requirement: REQ-WUI-RECOVERY-001 Recovery action errors are visible

The Web UI SHALL display retry mutation errors using the same issue action error display pattern used for rerun, start, close, and reopen errors. Retry API errors SHALL NOT be swallowed or hidden after the user clicks Retry.

#### Scenario: Retry error appears in action error area
- **WHEN** a user clicks Retry on Issue detail
- **AND** `POST /api/issues/:number/retry` returns a 409 or other error
- **THEN** the action error area displays the returned retry error message
- **AND** the user can still see and choose other available recovery actions

#### Scenario: Recovery actions share error display pattern
- **WHEN** Retry, Rerun Stage, Start, Close, or Reopen fails from Issue detail
- **THEN** the failure is shown through the same visible action error area
