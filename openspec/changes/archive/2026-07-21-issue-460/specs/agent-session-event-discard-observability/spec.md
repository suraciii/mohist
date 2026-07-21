### Requirement: Binding-rejected event batches are observable

When a non-empty AgentSession runtime-event batch requires the current physical runtime binding and its reported runtime session identity is missing or does not equal that binding, the system SHALL emit a warning-level log entry. The warning MUST identify the logical AgentSession, the current expected runtime session identity, the reported runtime session identity or its absence, and the total number of events discarded from the batch.

The system MUST continue to reject the entire batch without changing AgentSession state, persisting transcript content, publishing realtime transcript events, buffering the events, or scheduling a retry.

#### Scenario: Stale physical binding rejects a batch with diagnostics
- **WHEN** a non-empty runtime-event batch reports a physical runtime session identity different from the AgentSession's current binding
- **THEN** the system SHALL emit a warning identifying the logical AgentSession, both runtime session identities, and the batch's event count
- **AND** every event in the batch MUST remain rejected without state, transcript, or realtime publication effects

#### Scenario: Missing physical binding identity rejects a batch with diagnostics
- **WHEN** a non-empty runtime-event batch that requires binding validation has no reported runtime session identity
- **THEN** the system SHALL emit a warning identifying the logical AgentSession, the current expected identity, the absence of a reported identity, and the batch's event count
- **AND** every event in the batch MUST remain rejected without being buffered or retried

### Requirement: Unsupported transcript event types are observable

For each event type in an accepted runtime-event batch that is outside the transcript event-type allowlist, the system SHALL emit a warning-level log entry identifying the logical AgentSession, the exact unsupported event type, and the number of discarded events of that type. Those events MUST remain excluded from transcript persistence and realtime transcript publication, and the diagnostic behavior MUST NOT expand or otherwise change the allowlist.

Supported event types in the same batch SHALL continue through their existing processing. The system MUST NOT buffer, retry, collect a metric for, or trigger an alert from unsupported events as part of this capability.

#### Scenario: Mixed batch contains repeated unsupported events
- **WHEN** an accepted batch contains supported events and multiple events of the same unsupported type
- **THEN** the system SHALL emit a warning identifying the logical AgentSession, that unsupported type, and the number of events discarded for that type
- **AND** the unsupported events MUST NOT be persisted or published to realtime transcript consumers
- **AND** the supported events SHALL continue through their existing persistence and publication behavior

#### Scenario: Accepted batch contains only supported event types
- **WHEN** an accepted runtime-event batch contains only event types in the transcript allowlist
- **THEN** the system MUST NOT emit an unsupported-event discard warning
- **AND** the events SHALL continue through their existing processing behavior
