## ADDED Requirements

### Requirement: coder_recovery_status SSE event

The system SHALL emit a `coder_recovery_status` event via EventBus when a hang recovery lifecycle event occurs. The event payload SHALL include:

```
{
  issueId: string,        // String(issueNumber) — matches SSE convention
  projectId: string,
  executionId: string,
  acpSessionId: string,
  status: 'detected' | 'recovering' | 'recovered' | 'failed',
  attempt: number,        // 1-based recovery attempt number
  reason?: string         // only for status='failed' (e.g. 'cancel_timeout', 'max_attempts_exceeded')
}
```

The event SHALL be emitted at four points in the recovery lifecycle:
1. `detected`: when idle threshold is first exceeded
2. `recovering`: when the recovery prompt is issued (cancel succeeded)
3. `recovered`: when the agent produces the first event after recovery prompt
4. `failed`: when recovery fails (cancel timeout or max attempts exceeded)

#### Scenario: Hang detected event
- **WHEN** an ACP session has been idle for 3 minutes
- **THEN** EventBus emits `coder_recovery_status` with `status: 'detected'`, `attempt: 1`

#### Scenario: Recovery in progress event
- **WHEN** `connection.cancel()` succeeds and a recovery prompt is issued
- **THEN** EventBus emits `coder_recovery_status` with `status: 'recovering'`, `attempt: 1`

#### Scenario: Recovery succeeded event
- **WHEN** the agent produces the first `sessionUpdate` after a recovery prompt
- **THEN** EventBus emits `coder_recovery_status` with `status: 'recovered'`, `attempt: 1`

#### Scenario: Recovery failed event
- **WHEN** recovery fails due to cancel timeout
- **THEN** EventBus emits `coder_recovery_status` with `status: 'failed'`, `reason: 'cancel_timeout'`

### Requirement: coder_recovery_status registered in SSE event types

The `coder_recovery_status` event SHALL be included in all SSE event type registrations:
- `event-bus.ts` EventMap type definition
- `events.ts` `ALL_EVENT_TYPES` array (backend)
- `agent-events.ts` `AGENT_DETAIL_EVENTS` array (frontend)
- `useSSE.ts` `eventTypes` array (frontend)

#### Scenario: SSE client receives recovery status
- **WHEN** a WebUI SSE client is connected and a hang is detected
- **THEN** the client receives `event: coder_recovery_status` with the recovery lifecycle data

#### Scenario: All registration arrays in sync
- **WHEN** `coder_recovery_status` is added to one registration array
- **THEN** it SHALL be present in all 4 registration arrays
