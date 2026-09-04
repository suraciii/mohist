### Requirement: One persisted public event route per Session

`GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events` SHALL read one Session's durable public projection and MUST NOT read a Project-wide mixed stream. `after` SHALL be an optional opaque cursor and `limit` optional with a default of 100 and a maximum of 100. Resume SHALL be exclusively after: a page SHALL contain only events whose sequence is greater than the position encoded by `after`, and there SHALL be no implicit inclusive replay mode. A response page SHALL carry `sessionId`, `events` sorted ascending by sequence, `nextCursor`, and `highWaterSequence`. `nextCursor` SHALL equal the last event's cursor in a non-empty page and be the exclusive continuation position after that event; for an empty page it SHALL be positioned at the page's `highWaterSequence`. Each event SHALL carry `sequence`, `cursor`, `type`, and `occurredAt`.

#### Scenario: A first page is read without a cursor

- **WHEN** an authorized caller requests a Session's events without `after`
- **THEN** the Server SHALL return the earliest retained events of the current generation in ascending sequence order with a `nextCursor` after the last returned event

#### Scenario: Resume is exclusively after

- **WHEN** a page is requested with `after` set to a previously returned cursor
- **THEN** the page SHALL contain only events whose sequence is strictly greater than the position encoded by that cursor
- **AND** the event at the cursor position SHALL NOT be repeated

#### Scenario: Limit is bounded

- **WHEN** a request supplies `limit` greater than 100 or omits it
- **THEN** the Server SHALL return at most 100 events, using 100 as the default

### Requirement: A finite public event vocabulary

The public event vocabulary SHALL be exactly `input.accepted`, `input.rejected`, `turn.queued`, `turn.running`, `turn.outcome_pending`, `turn.terminal`, and `session.unknown` carrying execution, plus `session.context_reset`. Execution events SHALL carry an `execution` field that is exactly `PublicExecutionRead`, with no raw event data. `session.context_reset` SHALL be emitted only from a durable canonical ContextBoundary/Session reset fact, appended with the affected public snapshot and source checkpoint in one projection transaction, and SHALL carry only the smaller session payload — `projectId`, `agentId`, `sessionId`, `sessionActivity`, `admission`, `reasonCode` — with the outer `sequence` and `occurredAt` as its ordering and timestamp facts. A `session.context_reset` event MUST NOT carry `jobId`, `inputId`, `turnId`, `output`, `error`, prompt, memory, runtime, path, raw payload, or operation/binding data.

#### Scenario: An execution event carries only the public projection

- **WHEN** the journal emits `turn.queued` or another execution event
- **THEN** the event SHALL carry `sequence`, `cursor`, `type`, `occurredAt`, and an `execution` object that is exactly `PublicExecutionRead`
- **AND** it SHALL NOT carry any raw payload or unlisted property

#### Scenario: A context reset emits the smaller session payload

- **WHEN** a durable canonical Session reset fact is projected
- **THEN** the journal SHALL append one `session.context_reset` event with the session allowlist payload
- **AND** the event MUST NOT reference a Job, Input, Turn, output, error, prompt, memory, runtime, or operation

### Requirement: Opaque tamper-evident cursors bound to one stream

An event cursor SHALL be opaque, tamper-evident, and bound to its Project, Session, stream generation, and exclusive sequence position. A cursor that is malformed, tampered with, bound to another Project or Session, or bound to another stream generation SHALL return 400 `cursor_invalid` with no fallback, no silent translation, and no event read attempted. Clients SHALL treat the cursor as data, not as a parseable ID.

#### Scenario: A tampered cursor is rejected

- **WHEN** a request supplies a cursor whose content was modified or that cannot be decoded
- **THEN** the Server SHALL return 400 `cursor_invalid`
- **AND** no events SHALL be returned and no fallback position SHALL be used

#### Scenario: A cursor bound to another stream is rejected

- **WHEN** a cursor minted for another Project or Session is presented to this Session's events route
- **THEN** the Server SHALL return 400 `cursor_invalid`
- **AND** the Server MUST NOT translate it into this Session's stream

### Requirement: Per-Session strictly increasing sequences

Each Session's public sequence SHALL be a strictly increasing positive integer across all of its stream generations. The projector SHALL never reuse or renumber a sequence for that Session, and every event page SHALL be sorted ascending by sequence.

#### Scenario: Sequences never repeat for one Session

- **WHEN** the journal appends successive public events for a Session, including across generation changes
- **THEN** each event's sequence SHALL be strictly greater than every earlier sequence of that Session
- **AND** no sequence SHALL be reused or renumbered

### Requirement: Stream generations isolate rebuilds

The first committed public projection for a Session SHALL create stream generation one. Generation SHALL be stable across normal projector restart, crash recovery, outbox replay, and ordinary projection checkpoint advancement. A projection rebuild or restore MUST NOT mutate the live journal in place: it SHALL build a new generation from durable canonical and outbox inputs, persist that generation's snapshot and checkpoint, and then atomically make that generation current, preserving the Session's next global sequence allocator so a sequence is never reused even when the active generation changes. An old-generation cursor SHALL return 400 `cursor_invalid` and MUST NOT be silently translated into the rebuilt stream; the client then reloads its known public Input/Turn observations and obtains a new cursor from the current generation.

#### Scenario: Generation survives restart and replay

- **WHEN** the projector restarts or replays outbox input without a rebuild
- **THEN** the Session's stream generation SHALL remain unchanged

#### Scenario: A rebuild swaps generations atomically without sequence reuse

- **WHEN** a projection rebuild completes for a Session
- **THEN** the new generation SHALL become current atomically with its snapshot and checkpoint
- **AND** sequences allocated by earlier generations SHALL never be reused by the new generation

#### Scenario: An old-generation cursor is rejected

- **WHEN** a client presents a cursor minted by a superseded stream generation
- **THEN** the Server SHALL return 400 `cursor_invalid`
- **AND** the client SHALL be able to recover by reloading public Input/Turn observations and reading the current generation from its start

### Requirement: A retained-history floor with cursor_expired

V1 SHALL retain every public event while its AgentSession is retained; ordinary transcript compaction MUST NOT compact this public event stream, and there SHALL be no time-based public event compaction in v1. If a future retained-history operation reclaims a public prefix, it SHALL persist the current generation's `earliestSequence` floor in the same projection transaction as its retained snapshot and checkpoint. A valid current-generation cursor whose `after` sequence is earlier than that floor SHALL return 410 `cursor_expired` including the safe public `earliestSequence` and `latestSequence` bounds, and the caller reloads current Input/Turn observations before starting at a new retained position. The Server MUST NOT silently restart a `cursor_expired` or `cursor_invalid` request at the beginning of the stream or at the current head.

#### Scenario: Retention outlives transcript compaction

- **WHEN** a Session's transcript is compacted by ordinary internal compaction
- **THEN** the public event stream SHALL remain fully retained while the Session is retained

#### Scenario: A cursor before the retention floor expires

- **WHEN** a valid current-generation cursor resumes at a position earlier than the generation's `earliestSequence` floor
- **THEN** the Server SHALL return 410 `cursor_expired` with the safe `earliestSequence` and `latestSequence` bounds in the error envelope
- **AND** the Server MUST NOT silently restart the read at the stream start or head

### Requirement: Closed-stream tombstones

The direct API SHALL NOT expose an external Session delete route. When another authorized control-plane action deletes a Session, the Server SHALL close its public stream and retain a minimal cursor tombstone for the cursor-retention window. A valid current-generation cursor against a closed tombstone SHALL return 410 `cursor_expired` with `earliestSequence=null` and the last safe `latestSequence`. A request without a valid cursor against a closed stream SHALL return 404 `session_not_found`. After physical stream purge removes the tombstone, a cursor can no longer be recognized and SHALL return 400 `cursor_invalid`. A new logical Session always has a new SessionId and MUST NOT reuse a deleted stream.

#### Scenario: A cursor against a deleted Session expires

- **WHEN** a caller resumes with a valid current-generation cursor whose Session was deleted and whose tombstone is retained
- **THEN** the Server SHALL return 410 `cursor_expired` with `earliestSequence=null` and the last safe `latestSequence`

#### Scenario: A cursorless read of a deleted Session is 404

- **WHEN** a caller requests a deleted Session's events without a valid cursor
- **THEN** the Server SHALL return 404 `session_not_found`

#### Scenario: A purged tombstone is no longer recognizable

- **WHEN** the cursor-retention window has ended and the tombstone was physically purged
- **THEN** any presented cursor for that stream SHALL return 400 `cursor_invalid`

### Requirement: Documented dedup and ordering rules for resuming callers

The direct API SHALL document the caller-side resume rules, and the Server's delivery semantics SHALL make them sufficient: a retried GET MAY return the same page; concurrent page requests MAY arrive out of order; a client stores `nextCursor` only after it durably processes the page; the client deduplicates by `(sessionId, sequence)`, applies events in ascending sequence order, and does not infer a missing transition from a later sequence; when it observes a gap it resumes from its last contiguous cursor or rereads the target Input or Turn.

#### Scenario: A retried page may repeat events

- **WHEN** a caller retries the same GET after durably processing the earlier page
- **THEN** the Server MAY return the same events again
- **AND** the caller's documented `(sessionId, sequence)` dedup rule SHALL be sufficient to process the retry safely

#### Scenario: A gap is handled by resume, not inference

- **WHEN** a client observes a gap between its last contiguous cursor and a returned page
- **THEN** the documented rule SHALL be to resume from the last contiguous cursor or reread the target Input or Turn
- **AND** the client MUST NOT infer a missing transition from the later sequence

### Requirement: The stream is sourced only from the persisted projection

The events route SHALL be served exclusively from the persisted public Session event journal within the durable projection. The Server MUST NOT source the route from an in-memory event bus, SignalR hub, Runner notification, or UI timeline. Those channels MAY notify a client to reread the persisted route, but they MUST NOT define its cursor, ordering, generation, or payload.

#### Scenario: A transient notification does not advance the stream

- **WHEN** a Runner or in-memory notification arrives before the projection transaction commits
- **THEN** the events route SHALL reflect only events already persisted in the journal
- **AND** the notification SHALL NOT mint a cursor, sequence, or event payload for this route
