### Requirement: Durable per-Session public event stream

The Server MUST provide `GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events` as a read-only stream for exactly one authorized Session. The stream MUST be backed by a durable public projection rather than an in-memory event bus, SignalR delivery, Runner notification, UI timeline, internal transcript dump, or Project-wide mixed feed. Each page MUST contain `sessionId`, an ascending `events` collection, `nextCursor`, and `highWaterSequence`. Each event MUST contain its sequence, opaque cursor, type, occurrence timestamp, and either an allowlisted public execution observation or the separately constrained context-reset Session payload. Public execution events MUST be limited to `input.accepted`, `input.rejected`, `turn.queued`, `turn.running`, `turn.outcome_pending`, `turn.terminal`, and `session.unknown`.

#### Scenario: Read one Session after reconnect
- **WHEN** an authorized caller reads the event route for a known Session
- **THEN** the response contains only events for that Session, with public execution observations and no raw internal event payload

#### Scenario: Internal event delivery is delayed
- **WHEN** SignalR or an event-bus subscriber is delayed, duplicated, or offline while durable canonical facts are available
- **THEN** the persisted public event route remains the source of ordering and recovery, and the caller is not required to treat push delivery as the event log

### Requirement: Context reset boundary

The public stream MUST emit `session.context_reset` only for a durable canonical context-boundary fact. Its payload MUST expose only the Session ID, Project ID, Agent ID, Session activity, admission, and safe `context_reset` reason, plus the event sequence and timestamp. It MUST NOT expose prior context, prompt text, memory, Runtime details, workspace data, or operation and binding data.

#### Scenario: Durable reset is projected
- **WHEN** a Session reset or equivalent context boundary commits
- **THEN** the stream contains one ordered `session.context_reset` event describing the new public boundary without disclosing the previous context

#### Scenario: Non-durable reset observation
- **WHEN** a client or transport reports a reset that has not been committed as a canonical boundary
- **THEN** the Server does not append a public context-reset event

### Requirement: Exclusive opaque cursor semantics

The event route MUST accept an optional opaque `after` cursor and `limit`, with a default limit of 100 and a maximum of 100. A page requested with `after` MUST contain only events whose sequence is strictly greater than the cursor position. The cursor MUST be tamper-evident and bound to the Project, Session, stream generation, and exclusive sequence position; callers MUST treat it as opaque. `nextCursor` MUST represent the position after the last returned event, and an empty page MUST advance it to the page high-water sequence.

#### Scenario: Resume after the last processed event
- **WHEN** a caller requests a page with the cursor returned after sequence 18
- **THEN** the page contains no sequence 18 event and includes only events with sequence greater than 18, in ascending sequence order

#### Scenario: Empty page at the high water mark
- **WHEN** a caller requests events after the current highest sequence and no newer event exists
- **THEN** the Server returns an empty page whose `nextCursor` represents that high-water sequence without fabricating an event

### Requirement: Stable sequence and projection transaction

Each Session stream MUST use strictly increasing positive sequence values that are never reused or renumbered, including across projector restart, crash recovery, outbox replay, or stream-generation changes. The projector MUST commit the public snapshot, corresponding event entries, event identity, next sequence, and source checkpoint in one projection transaction. A crash before commit MUST leave no partial public event, snapshot, or checkpoint; a restart after commit MUST NOT append a duplicate for the same normalized source transition.

#### Scenario: Projector crash before commit
- **WHEN** projection processing crashes before its transaction commits
- **THEN** the next recovery reprocesses the source fact without exposing a partial event or checkpoint and produces at most one committed public sequence

#### Scenario: Projector restarts after commit
- **WHEN** the projector restarts after a public event and checkpoint have committed
- **THEN** it resumes after the checkpoint and preserves the existing sequence without emitting a duplicate event

### Requirement: Repeated and concurrent page reads

The Server MUST make repeated reads of a valid cursor safe and MUST preserve stable `(SessionId, sequence)` event identity. Concurrent pages are allowed to arrive out of order, but each page MUST remain ascending by sequence and MUST never silently rewrite or renumber an event. A caller that detects a gap MUST be able to reread from its last contiguous cursor or reread the affected public Input or Turn; the Server MUST NOT imply a missing transition from a later event.

#### Scenario: Retry after a lost page response
- **WHEN** a caller retries the same GET because the first page response was lost
- **THEN** the Server can return the same events and cursor, allowing the caller to deduplicate by Session ID and sequence without creating any execution effect

#### Scenario: Concurrent pages
- **WHEN** two requests from the same Session use cursors that produce overlapping or adjacent pages and responses arrive out of order
- **THEN** each response retains its own ordered sequence range and the caller can apply the events in sequence order without treating the later response as proof that an omitted event did not exist

### Requirement: Generation, invalidation, and retention errors

The Server MUST reject malformed, tampered, cross-Project, cross-Session, and old-generation cursors with `400 cursor_invalid` and MUST NOT fall back to the beginning or current head. A valid cursor before the retained history floor MUST return `410 cursor_expired` with only safe `earliestSequence` and `latestSequence` bounds. A caller MUST be required to reload current public observations before starting from a new retained position. Closed Session streams MUST retain a minimal cursor tombstone for the retention window and MUST not allow a new Session to reuse the deleted stream identity.

#### Scenario: Wrong-generation cursor
- **WHEN** a projection rebuild makes a new stream generation current and a caller submits a cursor from the old generation
- **THEN** the Server returns `400 cursor_invalid` without translating the cursor into the rebuilt stream

#### Scenario: Expired retained prefix
- **WHEN** a syntactically valid current-generation cursor points before the retained event floor
- **THEN** the Server returns `410 cursor_expired` with safe sequence bounds and does not silently restart the caller at sequence zero or the current head

#### Scenario: Closed stream tombstone
- **WHEN** an authorized control-plane action has closed a Session and the caller submits a valid cursor during the tombstone retention window
- **THEN** the Server returns `410 cursor_expired` with the last safe sequence, while a request without a valid cursor returns `404 session_not_found`

### Requirement: Projection lag and retained history

While a Session is retained, the v1 public stream MUST retain every public event; ordinary transcript compaction MUST NOT compact this stream. If an authorized request requires source facts beyond the committed projection checkpoint, the Server MUST return `503 projection_lag` and MUST NOT return a stale snapshot as current. The stream MUST remain independent of internal CloudEvent dispatch status and UI event-feed filtering.

#### Scenario: Canonical fact ahead of projection
- **WHEN** a Session outbox contains a required fact that the public projection has not committed
- **THEN** the event or execution read returns `503 projection_lag` with no new admission or execution effect, and a later retry can observe the committed projection

#### Scenario: Transcript compaction
- **WHEN** internal Session transcript compaction runs while the Session remains retained
- **THEN** previously committed public event sequences remain available for cursor-based resume
