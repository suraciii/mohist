## MODIFIED Requirements

### Requirement: Coder session detail exposes normalized transcript metadata

`GET /api/issues/:number/coder-sessions/:sessionId` SHALL provide the normalized transcript and display metadata required to render the session page without reconstructing core ordering, lifecycle state, or file-change metadata from raw event logs.

#### Scenario: Detail response contains stable transcript structure

- **WHEN** the client requests a coder session detail
- **THEN** the response includes normalized turns, assistant parts, transcript metadata, and incomplete markers sufficient for replay rendering

#### Scenario: Tool metadata is display-ready

- **WHEN** tool activity is included in the detail response
- **THEN** each tool part exposes stable status and enough metadata for display, including normalized identity and file-change details for patch/edit/write operations when available

#### Scenario: Replay remains usable without live SSE state

- **WHEN** a session is refreshed after completion or temporary disconnect
- **THEN** the detail response remains sufficient to render the same visible transcript order and grouping as the live session
