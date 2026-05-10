## MODIFIED Requirements

### Requirement: REQ-AR-001 Session liveness probing

Agent runtime SHALL track opencode ACP session liveness using session data timestamps and SHALL probe the same session after a quiet threshold before declaring the session failed.

#### Scenario: New ACP data keeps session running
- **WHEN** a running session receives any valid ACP/opencode session update, assistant text, tool update, message growth, or successful protocol response
- **THEN** `lastDataAt` SHALL be updated
- **AND** the session SHALL remain or return to `running`

#### Scenario: Quiet running session enters probing
- **WHEN** a running session has no valid new data for the configured quiet threshold
- **THEN** the session SHALL transition to `probing`
- **AND** Mohist SHALL send a probe to the same opencode session
- **AND** `probeSentAt` and `probeDeadlineAt` SHALL be recorded

#### Scenario: Probe receives data
- **WHEN** a probing session receives any valid ACP/opencode data before the probe deadline
- **THEN** the session SHALL transition back to `running`
- **AND** the task attempt SHALL continue waiting for normal completion

#### Scenario: Probe fails
- **WHEN** probe sending fails, the probe deadline expires, the ACP protocol disconnects, or the process exits unexpectedly
- **THEN** the session SHALL transition to `failed`
- **AND** the session call result SHALL include `success=false`, session failure metadata, and a failure reason

#### Scenario: Cancellation remains distinct
- **WHEN** the session is actively cancelled by user or abort signal
- **THEN** the session SHALL transition to `cancelled`
- **AND** the result SHALL NOT be classified as session liveness failure
