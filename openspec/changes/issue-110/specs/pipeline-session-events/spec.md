## ADDED Requirements

### Requirement: Compaction events are emitted as SSE events

The pipeline session event bridge SHALL emit `compaction_event` SSE events when compaction occurs in a session. The event SHALL carry the session identifier, pre-compaction context usage, post-compaction context usage, the strategy used, and a timestamp.

#### Scenario: compaction_event emitted during Plan stage compaction
- **WHEN** a Plan stage session undergoes compaction
- **THEN** EventBus SHALL emit `compaction_event` with `{ issueId, projectId, sessionId, contextWindowUsedBefore, contextWindowUsedAfter, strategy }`

#### Scenario: compaction_event emitted during Build stage compaction
- **WHEN** a Build stage task session undergoes compaction
- **THEN** EventBus SHALL emit `compaction_event` with `{ issueId, projectId, sessionId, taskExecutionId, contextWindowUsedBefore, contextWindowUsedAfter, strategy }`

#### Scenario: compaction_event registered in SSE event types
- **WHEN** the SSE event type registrations are inspected
- **THEN** `compaction_event` SHALL be present in `ALL_EVENT_TYPES`, `AGENT_DETAIL_EVENTS`, and the frontend `useSSE.ts` eventTypes array

### Requirement: Context health metric updates are emitted as SSE events

The pipeline session event bridge SHALL emit `context_health_update` SSE events when context window usage changes significantly (crosses a color threshold boundary or changes by more than 10 percentage points). The event SHALL carry the session identifier, current `contextWindowSize`, `contextWindowUsed`, and a derived health status (green/yellow/red).

#### Scenario: context_health_update emitted when crossing red threshold
- **WHEN** a session's context usage crosses from 79% to 82%
- **THEN** EventBus SHALL emit `context_health_update` with `{ issueId, projectId, sessionId, contextWindowSize, contextWindowUsed, healthStatus: "red" }`

#### Scenario: context_health_update emitted after compaction
- **WHEN** a session undergoes compaction reducing usage from 90% to 45%
- **THEN** EventBus SHALL emit `context_health_update` with `healthStatus: "green"`

#### Scenario: Small usage changes do not trigger events
- **WHEN** context usage changes from 45% to 47% (no threshold boundary crossed, <10pp change)
- **THEN** `context_health_update` SHALL NOT be emitted

#### Scenario: context_health_update registered in SSE event types
- **WHEN** the SSE event type registrations are inspected
- **THEN** `context_health_update` SHALL be present in all SSE event type registration arrays

### Requirement: Session liveness events include context health snapshot

Session lifecycle SSE events (`session_liveness` status changes) SHALL include the current context window usage as a snapshot. This provides context health data alongside liveness state transitions.

#### Scenario: Liveness event includes context usage
- **WHEN** a session transitions to `probing` and context usage is 78%
- **THEN** the `session_liveness` SSE event SHALL include `contextWindowUsed` and `contextWindowSize` fields
- **AND** the fields SHALL reflect the latest known values at transition time
