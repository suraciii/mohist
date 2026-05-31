## MODIFIED Requirements

### Requirement: REQ-AR-001 Session liveness probing

Agent runtime SHALL track opencode ACP session liveness using qualifying session activity timestamps and SHALL probe the same session after a quiet threshold before declaring the session failed. Qualifying session activity MUST include any ACP notification or protocol response that proves the session is alive and making observable progress, including `agent_message_chunk`, `agent_thought_chunk`, `tool_call`, `tool_call_update`, tool results, message growth, and successful protocol responses. Task output accumulation MAY remain limited to assistant answer text, but liveness MUST NOT depend on output text accumulation.

#### Scenario: New ACP activity keeps session running
- **WHEN** a running shared, resumed, new, or ephemeral ACP session receives qualifying session activity
- **THEN** `lastDataAt` SHALL be updated to the activity time
- **AND** the session SHALL remain or return to `running`

#### Scenario: Thought chunks keep session alive
- **WHEN** a running ACP session continuously receives `agent_thought_chunk` updates without `agent_message_chunk` updates
- **THEN** each thought chunk SHALL count as qualifying session activity
- **AND** the session MUST NOT fail with `Session liveness probe timed out` while the thought chunks continue within the quiet threshold or probe window

#### Scenario: Tool updates keep session alive
- **WHEN** a running ACP session receives `tool_call`, `tool_call_update`, or tool result updates
- **THEN** those updates SHALL count as qualifying session activity
- **AND** `lastDataAt` SHALL be updated

#### Scenario: Quiet running session enters probing
- **WHEN** a running session has no qualifying session activity for the configured quiet threshold
- **THEN** the session SHALL transition to `probing`
- **AND** Mohist SHALL send a probe to the same opencode session
- **AND** `probeSentAt` and `probeDeadlineAt` SHALL be recorded

#### Scenario: Probe receives activity
- **WHEN** a probing session receives qualifying session activity after the recorded probe version and before the probe deadline
- **THEN** the session SHALL transition back to `running`
- **AND** `lastDataAt` SHALL record the qualifying activity time
- **AND** the task attempt SHALL continue waiting for normal completion

#### Scenario: Probe fails
- **WHEN** probe sending fails, the probe deadline expires without qualifying session activity after the recorded probe version, the ACP protocol disconnects, or the process exits unexpectedly
- **THEN** the session SHALL transition to `failed`
- **AND** the session call result SHALL include `success=false`, session failure metadata, and a failure reason

#### Scenario: Cancellation remains distinct
- **WHEN** the session is actively cancelled by user or abort signal
- **THEN** the session SHALL transition to `cancelled`
- **AND** the result SHALL NOT be classified as session liveness failure
