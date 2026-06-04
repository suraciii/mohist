## ADDED Requirements

### Requirement: Session page loads metadata before raw events
The dedicated session page SHALL use the metadata-only session endpoint for initial page data and SHALL fetch raw session events separately only when transcript rendering needs them. The initial metadata fetch MUST NOT depend on server-projected transcript turns or embedded raw event logs.

#### Scenario: Initial session page request is metadata-only
- **WHEN** a user opens `/issues/51/workflow/sessions/T-003.1`
- **THEN** the page first requests `GET /api/issues/51/sessions/T-003.1`
- **AND** the response is used for title, status, model, stage, timestamps, and aggregate counts
- **AND** the page does not require `turns` or `workflowLogs` in that initial response

#### Scenario: Transcript events load on demand
- **WHEN** the session page needs to render the transcript body
- **THEN** it requests `GET /api/issues/:number/sessions/:name/events`
- **AND** it renders the transcript by projecting the returned raw events in the client

### Requirement: Session transcript projection is shared client-side
The session page SHALL derive chat transcript content from the shared client projection function `viewSessionEvents(events, 'chat')`. The page MUST NOT duplicate independent transcript reconstruction logic for historical refreshes.

#### Scenario: Refresh matches live transcript
- **WHEN** a live session event stream is later loaded after a page refresh
- **THEN** the session page projects the raw events through `viewSessionEvents(events, 'chat')`
- **AND** the visible transcript has equivalent turn order, assistant text, reasoning, and tool grouping to the live transcript for the same events

#### Scenario: Raw payloads are narrowed only in projection module
- **WHEN** the session page consumes session events with `payload: unknown`
- **THEN** event payload shape narrowing occurs inside the shared projection module
- **AND** UI components consume projected view data rather than parsing raw payloads independently

## REMOVED Requirements

### Requirement: Session page consumes server-projected transcript turns
The session page SHALL NOT depend on server-projected `turns[].assistant` data or an embedded `workflowLogs` field in session detail responses.

**Reason**: Server-projected transcript turns duplicate the live client projection and make initial session page loads unnecessarily heavy.

**Migration**: The page MUST consume metadata from `GET /api/issues/:number/sessions/:name`, raw events from `GET /api/issues/:number/sessions/:name/events`, and projected chat output from `viewSessionEvents(events, 'chat')`.

#### Scenario: Session page ignores removed transcript fields
- **WHEN** the metadata endpoint response has no `turns` and no `workflowLogs`
- **THEN** the session page still renders the session header from metadata
- **AND** it renders transcript content after loading and projecting raw events
