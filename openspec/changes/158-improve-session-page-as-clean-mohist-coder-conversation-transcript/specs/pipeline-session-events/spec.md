## MODIFIED Requirements

### Requirement: Live transcript convergence

Live session events SHALL update the same normalized transcript shape used by historical replay. SSE updates are an optimistic live view, and terminal or recovery lifecycle events SHALL reconcile the page with the canonical session detail transcript.

#### Scenario: Live tool updates merge in place

- **WHEN** live `coder_tool_call` start and update events arrive for the same id or inferable correlation key
- **THEN** the session page updates one existing logical tool part
- **AND** it does not append duplicate or orphan tool cards

#### Scenario: Live running state is restrained and accurate

- **WHEN** a session is actively streaming
- **THEN** only real non-terminal logical tools render as running
- **AND** pending or half-formed lifecycle fragments do not appear as separate visible tools

#### Scenario: Terminal events reconcile with persisted replay

- **WHEN** coder session completion, failure, timeout, cancellation, or recovery terminal events are observed
- **THEN** the page invalidates or refetches the session detail transcript
- **AND** the refetched historical transcript preserves equivalent visible order and grouping to the live view

#### Scenario: Live updates respect reader position

- **WHEN** the reader is near the bottom of the transcript
- **THEN** live text and tool updates follow the stream
- **WHEN** the reader has scrolled away from the bottom
- **THEN** live updates do not force-scroll and a new-content affordance is shown
