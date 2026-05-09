## MODIFIED Requirements

### Requirement: Coder session detail normalized transcript

The coder session detail API SHALL return a canonical normalized transcript that the session page can render without re-projecting raw stream logs. The response SHALL preserve existing session metadata while exposing normalized turns, merged tool parts, transcript warnings, changed-file summaries, and raw-debug access where available.

#### Scenario: Detail endpoint returns normalized transcript

- **WHEN** the client requests `GET /api/issues/:number/coder-sessions/:sessionId`
- **THEN** the response includes normalized Mohist/Coder turns with merged logical tool parts
- **AND** the response includes metadata for status, last activity, event count, tool count, turn count, changed files, warnings, and unknown-tool presence when available

#### Scenario: Historical replay uses persisted data

- **WHEN** the session has persisted `session_stream_log` rows
- **THEN** the endpoint assembles the transcript from `session_stream_log`
- **AND** it does not require in-memory SSE state to render the completed session

#### Scenario: Legacy fallback remains understandable

- **WHEN** no session stream rows exist but filtered workflow log stream events exist
- **THEN** the endpoint uses workflow log fallback events to assemble a best-effort transcript
- **AND** missing prompts or ambiguous normalization are surfaced as incomplete state or transcript warnings

#### Scenario: Running session metadata is not misleading

- **WHEN** a session is still running or finalizing
- **THEN** terminal fields such as completed timestamp and completed duration are not presented as completed-session facts
- **AND** the response still exposes last activity and current display status data for the live page
