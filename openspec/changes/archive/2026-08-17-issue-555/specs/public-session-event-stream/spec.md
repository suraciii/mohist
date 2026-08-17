### Requirement: The event route serves one Session's persisted stream

`GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events` SHALL
read only that Session's durable public projection, never a Project-wide
mixed stream. `after` is an optional opaque cursor and `limit` is optional
with a default of 100 and a maximum of 100. The route MUST be sourced from the
persisted projection only; an in-memory event bus, SignalR hub, Runner
notification, or UI timeline MUST NOT define its cursor, ordering,
generation, or payload, though such channels MAY notify a client to reread
this route.

#### Scenario: Limit is capped

- **WHEN** a caller requests the events route with `limit=500`
- **THEN** the Server returns at most 100 events in the page

#### Scenario: Delayed notification channels do not source the route

- **WHEN** a SignalR notification arrives before the durable public projection includes the corresponding fact
- **THEN** the events route still returns only what the persisted projection contains

### Requirement: Resume is exclusively after the cursor

The page SHALL contain only events whose sequence is greater than the position
encoded by `after`; there is no implicit inclusive replay mode. A request
without `after` returns events from the beginning of the retained stream. The
response SHALL include `sessionId`, the ascending `events` page,
`nextCursor`, and `highWaterSequence`. An event cursor is the exclusive
continuation position immediately after that event; `nextCursor` equals the
last event's cursor in a non-empty page, and for an empty page it is
positioned at the page's `highWaterSequence`.

#### Scenario: Cursor returns only newer events

- **WHEN** a caller resumes with `after` encoding sequence 18 and sequences 19 and 20 exist
- **THEN** the page contains exactly the events with sequences 19 and 20 in ascending order

#### Scenario: Empty page positions at the high water mark

- **WHEN** a caller resumes with `after` encoding the current `highWaterSequence`
- **THEN** the page is empty and `nextCursor` is positioned at that `highWaterSequence`

### Requirement: Event vocabulary and payloads are allowlisted

The execution event vocabulary SHALL be exactly `input.accepted`,
`input.rejected`, `turn.queued`, `turn.running`, `turn.outcome_pending`,
`turn.terminal`, and `session.unknown`; each of these events carries
`sequence`, `cursor`, `type`, `occurredAt`, and an `execution` object that is
exactly `PublicExecutionRead`, with no raw event data. `session.context_reset`
SHALL also be a public event, emitted only from a durable canonical
ContextBoundary/Session reset fact; it carries `sequence`, `cursor`, `type`,
`occurredAt`, and a smaller `session` payload of exactly `projectId`,
`agentId`, `sessionId`, `sessionActivity`, `admission`, and `reasonCode`. For
`session.context_reset` no `jobId`, `inputId`, `turnId`, `output`, `error`,
prompt, memory, runtime, path, raw payload, or operation/binding data MAY be
present.

#### Scenario: Execution events carry the public execution shape

- **WHEN** the projector appends a `turn.queued` event
- **THEN** the event's `execution` field is a `PublicExecutionRead` with the full allowlisted key set

#### Scenario: Context reset carries only the session payload

- **WHEN** a durable canonical Session reset fact is projected
- **THEN** the appended `session.context_reset` event contains only `projectId`, `agentId`, `sessionId`, `sessionActivity`, `admission`, and `reasonCode` in its session payload
- **AND** no Job, Input, Turn, output, or error field appears in it

### Requirement: Sequences are strictly increasing and clients deduplicate

Each Session's sequence SHALL be a strictly increasing positive integer across
all its stream generations; the Server MUST never reuse or renumber a
sequence, and an event page MUST be sorted ascending by sequence. Retrying a
GET MAY return the same page, and concurrent page requests MAY arrive out of
order, so the client MUST deduplicate by `(sessionId, sequence)`, apply events
in ascending sequence order, and MUST NOT infer a missing transition from a
later sequence; on observing a gap it resumes from its last contiguous cursor
or rereads the target Input or Turn. A client stores a cursor only after it
durably processes the page.

#### Scenario: Sequence never regresses across a rebuild

- **WHEN** a Session's projection rebuild switches to a new stream generation after sequence 183
- **THEN** the next appended event has a sequence greater than 183

#### Scenario: Duplicate page delivery is client-deduplicated

- **WHEN** a caller retries the same GET and receives the same events again
- **THEN** the Server returns the same page content and the client deduplicates by `(sessionId, sequence)` without inferring missing transitions

### Requirement: Cursors are opaque, bound, and tamper-evident

An event cursor SHALL be opaque and tamper-evident and SHALL be bound to its
Project, Session, stream generation, and exclusive sequence position; clients
treat it as data, not a parseable ID. A cursor that is malformed, tampered
with, undecodable, or bound to another Project, Session, or stream generation
SHALL return `400 cursor_invalid` with no fallback and no event read
attempted. An old-generation cursor is a wrong-generation cursor: it returns
`400 cursor_invalid` and MUST NOT be silently translated into the rebuilt
stream; the client then reloads its known public Input/Turn observations and
obtains a new cursor from the current generation.

#### Scenario: Tampered cursor is rejected without fallback

- **WHEN** a caller resumes with a cursor whose encoded position or binding was modified
- **THEN** the response is `400 cursor_invalid` and no events are returned for that request

#### Scenario: Cross-Session cursor is rejected

- **WHEN** a cursor issued for `session_a` is used against `session_b`
- **THEN** the response is `400 cursor_invalid`

#### Scenario: Old-generation cursor is not translated

- **WHEN** a rebuild switches the Session to generation two and the caller resumes with a generation-one cursor
- **THEN** the response is `400 cursor_invalid`
- **AND** the Server does not translate the cursor into the rebuilt stream

### Requirement: Expired cursors report safe sequence bounds

A valid current-generation cursor whose `after` sequence falls before the
retained public event floor SHALL return `410 cursor_expired` and include safe
public `earliestSequence` and `latestSequence` bounds. The Server MUST NOT
silently restart the read at the beginning of the stream or at the current
head for either an expired or an invalid cursor; the caller reloads current
Input/Turn observations before resuming at a new retained position.

#### Scenario: Expired cursor returns bounds

- **WHEN** the retained floor is sequence 120 and the caller resumes with a valid current-generation cursor encoding sequence 100
- **THEN** the response is `410 cursor_expired` with `earliestSequence=120` and `latestSequence` set to the current safe head

#### Scenario: No silent restart

- **WHEN** a cursor is expired or invalid
- **THEN** the response is `410 cursor_expired` or `400 cursor_invalid` respectively
- **AND** the Server returns no events and no substituted restart position

### Requirement: Retention and closed streams behave predictably

V1 SHALL retain every public event while its AgentSession is retained;
ordinary transcript compaction MUST NOT compact this public event stream, and
there is no time-based public event compaction in v1. There is no direct
external Session delete route. When another authorized control-plane action
deletes a Session, the Server closes its public stream and retains a minimal
cursor tombstone for the cursor-retention window: a valid current-generation
cursor against that closed tombstone returns `410 cursor_expired` with
`earliestSequence=null` and the last safe `latestSequence`; a request without
a valid cursor returns `404 session_not_found`. After physical stream purge
removes the tombstone, a cursor can no longer be recognized and returns `400
cursor_invalid`. A new logical Session always has a new SessionId and cannot
reuse a deleted stream.

#### Scenario: Deleted Session with a valid cursor

- **WHEN** a Session is deleted by an authorized control-plane action and the caller resumes with a valid current-generation cursor during the tombstone window
- **THEN** the response is `410 cursor_expired` with `earliestSequence=null` and the last safe `latestSequence`

#### Scenario: Deleted Session without a cursor

- **WHEN** a caller requests events for a deleted Session without a valid cursor
- **THEN** the response is `404 session_not_found`

#### Scenario: Purged tombstone cursor is unrecognized

- **WHEN** the physical stream purge has removed the tombstone and the caller presents the old cursor
- **THEN** the response is `400 cursor_invalid`
