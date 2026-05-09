## MODIFIED Requirements

### Requirement: REQ-PSE-001 Session liveness status is emitted to live clients

Session lifecycle event streams SHALL surface current session call liveness state so live clients can render the simplified session status.

#### Scenario: Probing status emitted
- **WHEN** a running session transitions to `probing`
- **THEN** an SSE event SHALL be emitted with session identifiers, status `probing`, `lastDataAt`, `probeSentAt`, and `probeDeadlineAt`

#### Scenario: Running recovery emitted
- **WHEN** a probing session receives valid new data and returns to `running`
- **THEN** an SSE event SHALL be emitted with status `running` and the updated `lastDataAt`

#### Scenario: Failure status emitted
- **WHEN** a session becomes `failed` due to probe timeout, probe send failure, protocol disconnect, or process exit
- **THEN** an SSE event SHALL be emitted with status `failed` and `failureReason`

#### Scenario: Recovery event not reused
- **WHEN** session liveness probing emits status changes
- **THEN** the event SHALL NOT use recovery-specific semantics such as `coder_recovery_status`
