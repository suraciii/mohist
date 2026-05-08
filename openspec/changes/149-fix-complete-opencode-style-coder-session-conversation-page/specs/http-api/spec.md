## MODIFIED Requirements

### Requirement: Coder session detail exposes normalized transcript

`GET /api/issues/:number/coder-sessions/:sessionId` SHALL return the normalized transcript and display metadata needed by the session page without requiring the frontend to reconstruct ordering or tool identity from raw logs.

#### Scenario: Detail response includes enriched transcript data

- **WHEN** the client requests a coder session detail
- **THEN** the response includes enriched metadata, normalized turns, incomplete marker, transcript warnings, changed files, and unknown-tool status

#### Scenario: Persisted session replay works without SSE state

- **WHEN** a session has completed and no in-memory SSE state exists
- **THEN** the detail response is sufficient to render the full transcript

#### Scenario: Legacy fallback preserves readable history

- **WHEN** session stream logs are unavailable or incomplete
- **THEN** the endpoint falls back to compatible workflow log stream events
- **AND** legacy sessions without prompts return an explicit incomplete prompt fallback instead of empty transcript output

#### Scenario: Running sessions do not report terminal completion

- **WHEN** a session is not terminal
- **THEN** `completedAt` in display metadata is null
- **AND** terminal completion data is only exposed for completed, failed, timeout, or cancelled sessions
