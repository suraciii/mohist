### Requirement: Live transcript events use the strongest available session identity

The active session page SHALL display realtime transcript events that can be unambiguously associated with the visible logical session. When the current physical runtime identity is available, matching MUST use that identity together with the logical session identity. When the physical identity is temporarily unavailable, matching SHALL fall back to available logical session context, such as the stable AgentSession identity or the workflow session's project, issue, and session name.

#### Scenario: Physical and logical identities match
- **WHEN** a realtime transcript event carries the visible session's logical identity and current physical runtime identity
- **THEN** the page SHALL apply the event to the visible transcript

#### Scenario: Physical identity is temporarily unavailable
- **WHEN** the active session page does not yet have a physical runtime identity and a realtime transcript event unambiguously matches the visible logical session context
- **THEN** the page SHALL apply the event to the visible transcript
- **AND** the transcript MUST NOT remain empty solely because the physical runtime metadata is unavailable

#### Scenario: Logical session ID is unavailable for a workflow event
- **WHEN** an active workflow session event lacks a logical AgentSession ID but matches the visible project, issue, and workflow session name
- **THEN** the page SHALL apply the event to that workflow session's visible transcript

### Requirement: Fallback matching preserves session and runtime isolation

Logical-context fallback MUST NOT apply when an event is known to belong to another logical session or another physical runtime. Fallback matching MUST also be disabled for an explicitly selected historical runtime view, because live events describe the active runtime rather than the historical transcript.

#### Scenario: Event identifies another logical session
- **WHEN** a realtime event carries a logical session identity that differs from the visible session
- **THEN** the page MUST ignore the event
- **AND** matching project, issue, or session-name fields MUST NOT override the conflicting logical identity

#### Scenario: Event identifies another physical runtime
- **WHEN** the visible session and realtime event both provide physical runtime identities and those identities differ
- **THEN** the page MUST ignore the event
- **AND** logical-context fallback MUST NOT override the physical runtime mismatch

#### Scenario: Historical runtime is explicitly selected
- **WHEN** the user is viewing an explicitly selected historical runtime
- **THEN** active-runtime realtime events MUST NOT be appended to the historical transcript
- **AND** missing metadata MUST NOT enable logical-context fallback for that historical view

#### Scenario: Available identity is ambiguous
- **WHEN** a realtime event lacks enough physical and logical context to identify the visible session unambiguously
- **THEN** the page MUST ignore the event
