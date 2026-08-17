### Requirement: Projection commits snapshot, journal, and checkpoint atomically

The Server SHALL own one durable public projection per target Session (or per
launch target before a Session exists), whose inputs are the canonical
AgentJob/AgentSession/SessionInput/AgentTurn records and their durable outbox
facts. In one projection transaction the projector MUST persist all of: the
allowlisted `PublicExecutionRead` snapshot for every affected public target;
the corresponding public Session event journal entries and sequences; and the
source checkpoint/watermark proving which durable outbox facts the snapshot
and journal include. `PublicExecutionRead` and `PublicEventPage` MUST be read
only from this projection, never from a partial combination of canonical
aggregates, and an internal outbox delivery MUST NOT be turned directly into
an external event payload.

#### Scenario: Snapshot and events agree at one checkpoint

- **WHEN** the projector persists a `turn.terminal` public event for a Session
- **THEN** the same transaction persists the matching `PublicExecutionRead` snapshot and the source checkpoint that includes it
- **AND** a subsequent read of that Job and that Session's events returns mutually consistent facts

### Requirement: Prepared launches project from the Job anchor

A launch target SHALL be permanently anchored by `jobId` so its projection is
addressable before and after Session acceptance. After the canonical Job
prepare fact the projector MAY publish a Job-anchored `accepted` state with
null live Session/Input/Turn IDs; it MUST wait for the matching Session
acceptance or rejection fact before publishing a joined
Job/Session/Input/Turn mapping, and then update that same Job anchor with the
public references. A follow-up projection MUST wait for the matching canonical
Session Input/Turn fact.

#### Scenario: Prepared Job projects before Session acceptance

- **WHEN** a canonical Job prepare fact is durable but Session acceptance has not occurred
- **THEN** the public Job projection reports `status=accepted` and `jobStatus=preparing` with null `sessionId`, `inputId`, and `turnId`

#### Scenario: Joined mapping replaces null live IDs after acceptance

- **WHEN** the matching Session acceptance fact becomes durable
- **THEN** the projector publishes the joined Job/Session/Input/Turn mapping on the same Job anchor

### Requirement: Reads ahead of the checkpoint return projection lag

When an authorized route knows a required source watermark is ahead of the
stored projection checkpoint, it SHALL return `503 projection_lag` and MUST
NOT return a stale state as current. The caller retries the same key or read;
no new admission or effect occurs on the lag path. Projection lag is a
transport/reconciliation condition and MUST NOT be reported as the public
five-state `unknown`.

#### Scenario: Lagging projection answers 503, then recovers

- **WHEN** a caller re-reads a Job whose required durable outbox facts are ahead of the stored projection checkpoint
- **THEN** the response is `503 projection_lag` and no admission effect occurs
- **AND** after the projector catches up, the same read returns the current projection rather than the stale one

### Requirement: unknown is emitted only from consumed durable facts

The aggregate state `unknown` SHALL be emitted only when the projector has
consumed the required durable facts and those facts say that acceptance,
dispatch, binding, stop, or outcome cannot yet be confirmed. A confirmed
canonical terminal rejection needs no Turn fence.

#### Scenario: Unknown reflects facts, not projection backlog

- **WHEN** the projector has consumed all required durable facts for a Turn and the facts show its stop outcome cannot be confirmed
- **THEN** the public projection reports `status=unknown` with the applicable component fact set to `unknown` and `admission=blocked`

### Requirement: Terminal fences protect public terminal facts

A Turn terminal projection SHALL store the canonical terminal fence/revision
internally and may become terminal only after the current terminal fact passes
that fence. Later stale outbox facts, delayed Runner results, or replayed
projector input MUST NOT move that target back to a non-terminal public state
or replace its output, error, or sequence. Execution completion and stop race
through the same terminal fence: whichever terminal fact wins emits at most
one terminal public event.

#### Scenario: Late Runner result cannot revert a terminal Turn

- **WHEN** a Turn has projected a fenced terminal outcome and a delayed Runner result later arrives
- **THEN** the public projection remains terminal with the original outcome, output, error, and sequence

### Requirement: Projection crash recovery is checkpoint-based

The projection checkpoint, snapshot, event entries, event identity, and next
sequence SHALL be committed together. A crash before that transaction commits
MUST leave no partial snapshot, sequence, or checkpoint, and restart SHALL
replay the same durable outbox input. A crash after commit SHALL resume after
the checkpoint and MUST NOT emit a second public sequence for the same
normalized source transition. This recovery MUST NOT replay a Runner, launch,
follow-up, or stop effect.

#### Scenario: Crash before commit replays cleanly

- **WHEN** the projector crashes before a projection transaction commits
- **THEN** no partial snapshot, journal sequence, or checkpoint is visible after restart
- **AND** replaying the same durable outbox input produces the same projection outcome

#### Scenario: Crash after commit does not duplicate sequences

- **WHEN** the projector crashes after a projection transaction commits and restarts
- **THEN** it resumes after the checkpoint without emitting a second public sequence for the same normalized source transition

### Requirement: Stream generations switch atomically and preserve sequences

The first committed public projection for a Session SHALL create stream
generation one. Generation SHALL be stable across projector restart, crash
recovery, outbox replay, and ordinary checkpoint advancement. A projection
rebuild or restore MUST NOT mutate the live journal in place: it builds a new
generation from durable canonical/outbox inputs, persists its snapshot and
checkpoint, then atomically makes that generation current, while preserving
the Session's next global sequence allocator so a sequence is never reused
when the active generation changes.

#### Scenario: Ordinary restart keeps generation one

- **WHEN** the Server restarts and replays outbox input for an existing projected Session
- **THEN** the Session's active stream generation is unchanged and no sequence is renumbered

#### Scenario: Rebuild switches generation without sequence reuse

- **WHEN** a projection rebuild builds a new generation for a Session whose last published sequence was 183
- **THEN** the new generation becomes current atomically and its first new event receives sequence 184 or later
