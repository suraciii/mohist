## MODIFIED Requirements

### Requirement: Live transcript updates converge with replayed transcript structure

Live SSE updates and replayed session detail SHALL converge on the same visible transcript structure so refresh does not materially change reasoning order, tool identity, or changed-file summaries.

#### Scenario: Live thinking streams inline with text

- **WHEN** a running session emits assistant text and thinking updates
- **THEN** the live transcript appends thinking inline using the same part-boundary semantics as replayed transcript assembly
- **AND** refreshing after completion preserves materially the same ordering

#### Scenario: Live terminal reconciliation preserves transcript shape

- **WHEN** completion, failure, timeout, cancellation, or recovery events occur during a live session
- **THEN** the frontend reconciles to the persisted transcript without losing inline thinking, tool updates, or changed-file summaries

### Requirement: Realtime session events include dedicated thinking chunks

The observer-to-SSE pipeline SHALL expose a first-class `coder_thought_chunk` event for visible sessions.

#### Scenario: Thinking chunks are emitted end-to-end

- **WHEN** the runtime observes a reasoning/thought chunk for a visible session
- **THEN** workflow observers emit `coder_thought_chunk`
- **AND** the event is registered in backend and frontend event registries so the Web UI can consume it live
