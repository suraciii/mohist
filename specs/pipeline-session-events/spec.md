## MODIFIED Requirements

### Requirement: REQ-PSE-001 Session liveness status is emitted to live clients

Session lifecycle event streams SHALL surface current session call liveness state so live clients can render the simplified session status and explain probe outcomes. Liveness status payloads MUST include enough probe state to identify when a probe was sent, when it would expire, the last qualifying session activity known to the runner, and whether qualifying activity arrived after the active probe was recorded.

#### Scenario: Probing status emitted
- **WHEN** a running session transitions to `probing`
- **THEN** an SSE event SHALL be emitted with session identifiers, status `probing`, `lastDataAt`, `probeSentAt`, and `probeDeadlineAt`
- **AND** the event SHALL include the active probe version or equivalent correlation state used to decide whether later activity satisfies the probe

#### Scenario: Running recovery emitted
- **WHEN** a probing session receives qualifying session activity and returns to `running`
- **THEN** an SSE event SHALL be emitted with status `running` and the updated `lastDataAt`
- **AND** the event SHALL expose the last qualifying activity type and timestamp that satisfied the probe when available

#### Scenario: Failure status emitted
- **WHEN** a session becomes `failed` due to probe timeout, probe send failure, protocol disconnect, or process exit
- **THEN** an SSE event SHALL be emitted with status `failed` and `failureReason`
- **AND** probe timeout failure metadata SHALL include `probeSentAt`, `probeDeadlineAt`, `lastDataAt`, and the last qualifying activity type or equivalent evidence when available

#### Scenario: Timeout explains missing post-probe activity
- **WHEN** a liveness probe times out
- **THEN** session metadata or emitted lifecycle events SHALL make it clear that no qualifying session activity arrived after the active probe was recorded and before `probeDeadlineAt`

#### Scenario: Recovery event not reused
- **WHEN** session liveness probing emits status changes
- **THEN** the event SHALL NOT use recovery-specific semantics such as `coder_recovery_status`
