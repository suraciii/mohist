# External Agent API

The private `/api/v1` API gives a trusted external caller a narrow way to
launch, continue, observe, and stop Mohist Agent work. It adapts canonical
execution state and makes response loss recoverable without exposing internal
execution facts or creating a second lifecycle.

The product route catalog and public fields live in
[`../docs/agent-api.md`](../docs/agent-api.md). This design owns idempotency,
projection consistency, public-state precedence, cursor behavior, and the
boundary between direct callers and canonical execution.

## Core Decisions

- A direct caller authenticates with a Bearer PAT and an explicit private
  Project grant. A caller-supplied Connection identity cannot replace it.
- Server alone owns AgentJob, AgentSession, SessionInput, AgentTurn, admission,
  queue, operation, and terminal facts. The API returns public observations.
- The API reuses the canonical Agent launch and Session boundary. It creates no
  second queue, Runtime Session, transcript, or client-owned event log.
- Every write is keyed by a caller Idempotency-Key. Server normalizes the
  accepted request and computes the fingerprint; callers never supply a hash.
- Public reads expose one aggregate state plus safe component facts. They never
  serialize an internal read model, prompt, transcript, workspace, Runner, or
  provider fact.
- A Session event stream is a durable public projection. It is not an internal
  event bus, Runner stream, transcript dump, or client-side timeline.
- `unknown` means a consumed canonical fact is unresolved. It never authorizes
  automatic replay or creation of replacement work.

## System Boundary

```text diagram
                    +-----------------+
                    | external caller |
                    +--------+--------+
                             |
                             vPAT + Project grant
                   +-------------------+
                   | /api/v1 Agent API |
                   +---------+---------+
              +--------------+--------------+
              v                             v
 +-------------------------+   +-------------------------+
 |     canonical Agent     |   |    public projection    |
 | execution Job / Session |   | snapshot / event cursor |
 |     / Input / Turn      |   +-------------------------+
 +-------------------------+
```

The ownership split is deliberate:

- [Agent execution](agent-execution.md) owns AgentJob, AgentSession,
  SessionInput, AgentTurn, admission, and recovery.
- [Conventions](conventions.md) owns canonical internal read shapes and effect
  fences.
- [Authentication and identity](auth.md) owns PAT identity, scopes, and Project
  grants.
- The [product API reference](../docs/agent-api.md) owns routes, public fields,
  request and response shapes, and error codes.
- [Subagent cascade stop](subagents.md#cascade-stop) owns product-level root
  Session cascade stop.

The API has no endpoint for choosing a Runner, Runtime, Workspace, physical
Runtime Session, prompt memory, model, Instructions, Skills, or provider
operation. The selected Agent and canonical Session determine those facts. A
trusted Agent Connection is a separate adapter and cannot substitute for the
PAT boundary.

This contract adds only the credential-bound private-Project grant needed by
direct callers. It does not add encryption at rest, cross-user transcript
visibility, multi-tenant policy, OAuth clients, or general RBAC.

## HTTP Surface

All routes are under `/api/v1` and use canonical Mohist IDs, not display names.
Writes require `operator`; reads require `readonly` or `operator`. Launch,
follow-up, and stop return `200 OK` when their durable keyed outcome is known;
`200` does not mean execution has finished. A still-converging command may
return the documented retryable `503`.

The public route catalog in [`../docs/agent-api.md`](../docs/agent-api.md)
contains the exact paths and wire shapes. The API exposes only launch,
follow-up, stop, Job/Input/Turn reads, and one Session event stream. There is no
generic Session, Runner, Runtime, transcript, operation, or internal-event export
route. The Job route is a constrained public projection, not a serialized
`AgentJobLaunchRead`.

### Write request bodies

Launch and follow-up accept exactly `{"text":"..."}`: text is a non-empty
JSON string, whitespace is significant, and the value is retained as canonical
Input but never returned in a direct API response or public event. Attachments,
arbitrary context references, and caller-selected execution options are not
silently accepted or ignored. Stop accepts an empty body.

`Idempotency-Key` is a printable ASCII header of 1 to 128 characters. It is not
a JSON field, trace ID, Input ID, or Server-generated operation ID. On stop it
is the caller-visible operation key.

## Authentication and admission order

PAT issuance for this boundary requires an authenticated issuer and either an
explicit Project binding (`--project`) or an explicit `operator_all` grant
(`--scope operator --all-projects`). A failed binding returns `403` and
persists neither a Credential nor Project grant. The grant model and token
lifecycle remain authoritative in [auth.md](auth.md#externalagentcaller).

Every route applies this order:

1. Authenticate the Bearer PAT and resolve Principal, caller key, scopes, and
   Project grant.
2. Authorize the route scope and selected private Project. Agent, Job, Session,
   Input, and Turn routes also authorize canonical Project membership.
3. Validate route, header, query, and JSON syntax without creating domain state.
4. Normalize the complete allowed write payload and compute its fingerprint.
5. Atomically look up the idempotency mapping, then perform canonical admission
   only when no matching mapping exists.

Authentication and authorization failures are terminal before lookup or
admission. `401` and `403` paths do not read or write an idempotency mapping,
create a rejection tombstone, reserve execution records, write an outbox item,
append a public event, or issue a Runner/provider operation. An out-of-grant
Project is `403` even when the Project does not exist. A missing or foreign
resource is `404` only after Project authorization passes.

The direct API accepts a Bearer PAT only. Cookie-based Web sessions and trusted
Agent Connection identities remain separate adapters and cannot bypass this
requirement.

## Normalized fingerprint and idempotency

The Server parses accepted JSON once and creates a versioned canonical
representation. It preserves text exactly after JSON parsing and does not trim,
case-fold, or otherwise equate distinct prompts. Canonical property ordering and
canonical IDs make the representation deterministic. Server persists only the
fingerprint with the durable mapping and does not expose it or the raw request.

The command scopes and fingerprint inputs are:

- **Launch:** `(projectId, agentId, Idempotency-Key)`, plus contract version,
  command, canonical IDs, and the complete accepted body. A matching retry
  returns the original Job/Session/Input/Turn mapping and current public
  observation. A different fingerprint returns `409 idempotency_key_reused` and
  creates no canonical record, event, queue entry, outbox item, or external
  effect.
- **Follow-up:** `(sessionId, Idempotency-Key)`, plus contract version,
  command, canonical Session ID, and complete body. A matching retry returns
  the original Input/Turn mapping or durable rejection and its current public
  observation. A different fingerprint returns the same `409` with no new
  Input, Turn, queue entry, outbox item, or external effect.
- **Stop:** `(turnId, Idempotency-Key)`, plus contract version, command,
  canonical Turn ID, and empty body. A matching retry returns the original
  target Turn observation. A different fingerprint returns the same `409` with
  no new stop operation or external effect.

A stop mapping additionally binds callerKeyId, canonical Project ID, Session ID,
and Turn ID so one caller cannot replay another caller's public key. A follow-up
resolves its Project and Agent from the canonical Session; the body cannot name
either or influence the fingerprint with a derived value.

The mapping is durable before a successful command response. The first launch
creates at most one Job/Session/Input/Turn group. A follow-up creates at most
one Input/Turn pair. A definitive admission rejection is durable under the same
key and remains the same rejection after capacity recovers. Matching retries
never mint different IDs or another execution.

## Public execution projection

`PublicExecutionRead` is the only execution-shaped object returned by command
and resource-read routes. It is a strict allowlist, not a serialized
`AgentJobLaunchRead`, `AgentSessionRead`, `SessionInputRead`, `TurnResultRead`,
or `SessionOperationRead`. The product reference owns its exact field list.

Every listed key is present. IDs and timestamps are null only when their
canonical fact does not exist. A prepared launch may expose a Job ID with null
live IDs while Session acceptance is pending. `sequence` is null only when no
Session public event can exist. No response contains an unlisted execution
property.

The allowlist excludes runtimeSessionId, Runner IDs, Runtime names, binding
epochs, Connection IDs, leases, fences, operation IDs, attempt IDs,
dispatch/retry details, prompt or Input text, Instructions, memory, tool state,
Workspace/workdir/path, attachments, raw payloads, raw transcript facts, and
raw provider or Runner errors. A safe reasonCode or error may say
`queue_full`, `context_reset`, or `stop_outcome_unknown`; it cannot include
private cause detail.

## Projection consistency and recovery

AgentJob and AgentSession do not share a cross-aggregate transaction. Their
canonical records and durable outboxes remain the source of truth in
[agent-execution.md](agent-execution.md). The API must not claim that a combined
Job/Session/Input/Turn write and its public event are atomically committed.

Server owns one durable public projection per target Session, or per launch
target before a Session exists. A launch target remains addressable by its Job
ID before and after Session acceptance. A projector normalizes canonical
records and durable outbox facts and commits these in one projection
transaction:

1. The allowlisted `PublicExecutionRead` snapshot for every affected target.
2. The corresponding public Session event entries and sequences.
3. The source checkpoint or watermark proving which durable facts are included.

Reads come only from this projection. Snapshot and event page are mutually
consistent at their recorded checkpoint but eventually consistent with the
independent canonical aggregates. They never read a partial Job/Session
combination directly or turn internal outbox delivery into an external event.

A prepared launch may publish Job-anchored `accepted` with null live IDs after
its Job prepare fact. The projector waits for matching Session acceptance or
rejection before publishing the joined mapping, then updates the same Job
anchor. A follow-up waits for its Session Input/Turn fact. When an authorized
route requires a source watermark ahead of the stored checkpoint, it returns
`503 projection_lag` with `Retry-After` and no stale body. The caller retries the
same key or read. Projection lag is not public `unknown`.

`unknown` is emitted only after required durable facts are consumed and those
facts cannot yet confirm acceptance, dispatch, binding, stop, or outcome. A
confirmed terminal rejection needs no Turn fence. A Turn terminal projection
stores its canonical terminal fence/revision internally and becomes terminal
only after that fact passes the fence. Stale outbox facts, delayed Runner
results, and replayed projector input cannot reopen it.

The projection checkpoint, snapshot, event entries, event identity, and next
sequence commit together. A crash before commit leaves no partial snapshot,
sequence, or checkpoint. Restart replays the same durable input. A crash after
commit resumes after the checkpoint and cannot emit a duplicate public sequence.
This is projection recovery, not replay of a Runner, launch, follow-up, or stop
effect.

Input, Turn, and Job anchor IDs are globally unique canonical identities. Before
upsert, the projector resolves every complete `(anchorType, anchorId)` key for the
target, including rows currently owned by another Session. A row with a different
non-null Session owner is an explicit canonical collision: the projector retains
that owner and its terminal fence, emits no replacement snapshot for the
conflicting owner, and records the conflict for operators. It never transfers a
terminal anchor between Sessions.

An anchor collision does not roll back unrelated projection work. The conflicting
target consumes its canonical source watermarks without changing the occupied
anchor, so later targets can advance on the same or next sweep. This ordinary
checkpointed sweep is the controlled reconciliation path for identities created
before global uniqueness was enforced; operators do not delete canonical Job or
Session data. A Session generation rebuild uses the same owner rule and remains
available when its public event stream itself must be reconstructed.

## Five-state mapping and precedence

`status` is a projection over canonical facts and never replaces Job, Session,
Input, or Turn state. Component fields preserve blocked, rejected, and
outcome-pending detail.

- **accepted:** a Job is durably prepared, or an Input is durably accepted, but
  no current work is queued, running, outcome-pending, terminal, or unknown.
  Known IDs are present where applicable, and an existing Input has
  `inputStatus=accepted`.
- **queued:** the current Job or Turn is canonically queued without an
  unresolved fact or terminal fence. `jobStatus=queued` or `turnStatus=queued`;
  a retryable dispatch block remains queued with `admission=blocked` and public
  error.
- **running:** the current Job or Turn is running, or the Turn is
  `outcome_pending`, without an unresolved fact or terminal fence.
  `turnStatus=running` or `outcome_pending`; `outcome_pending` always has
  `admission=blocked` and no final output.
- **terminal:** a durable Input rejection, Job terminal outcome, or Turn
  terminal outcome exists. `inputStatus=rejected` has `outcome=rejected`, and
  terminal Job or Turn work has `completed`, `failed`, `cancelled`, or `blocked`
  outcome.
- **unknown:** acceptance, dispatch, binding, stop, or outcome cannot be
  confirmed and no fenced terminal fact resolves the target. At least one
  applicable `jobStatus`, `sessionActivity`, `inputStatus`, or `turnStatus` is
  unknown, and `admission=blocked` when a Session exists.

An Input or Turn read remains terminal even when its Session has a later active
Turn. An active Session does not turn a terminal Job or Turn into running.
`sessionActivity` is context, not the requested Input/Turn outcome.

Precedence is fixed:

1. A durable terminal fact protected by the target Turn's fence wins. Late
   Runner, stop, or event-bus observations cannot replace its output, error, or
   sequence.
2. A durable rejection is terminal with `outcome=rejected`, even without live
   Input or Turn ID.
3. Without a terminal fact, an unresolved canonical acceptance, dispatch,
   binding, stop, or outcome fact is `unknown`.
4. `outcome_pending` is running, never terminal, and blocks admission.
5. A retryable dispatch block is queued with `admission=blocked`; only a
   terminal blocked outcome is terminal.
6. Otherwise running wins over queued, and queued wins over accepted.

`unknown` and `outcome_pending` never authorize automatic replay. The Server
reconciles an existing durable operation only where canonical lifecycle permits
it. A reconnect, poll, or different key never creates a new Job, Input, Turn,
dispatch attempt, or stop.

## Public errors

[External Agent API](../docs/agent-api.md#projection-freshness-and-errors) owns
the public error envelope and status codes. Every rejection remains stable
under keyed replay and creates no effect beyond the durable decision already
returned.

## Persisted public Session events

### Scope and shape

The event route reads one Session's durable public projection, never a
Project-wide mixed stream. The product reference owns event names, page shape,
limits, and cursor behavior. Execution events carry the same public execution
projection; `session.context_reset` carries only its documented six-key Session
object and is sourced from a durable context-boundary fact.

An event cursor is an exclusive continuation position. `nextCursor` equals the
last event cursor in a non-empty page and `highWaterSequence` in an empty
page. The projector appends an event only in the same projection
transaction as its snapshot and source checkpoint. Each Session sequence is a
strictly increasing positive integer across stream generations. It never reuses
or renumbers a sequence, and pages sort ascending by sequence. The cursor is
opaque, tamper-evident, and bound to Project, Session, stream generation, and
exclusive sequence position.

### Stream generation and lifecycle

The first committed public projection creates stream generation one. Normal
restart, crash recovery, outbox replay, and checkpoint advancement preserve the
generation. A rebuild or restore creates a new generation from canonical and
outbox inputs, commits its snapshot and checkpoint, then atomically makes it
current. The global sequence allocator remains, so sequences are never reused.

An old-generation cursor returns `400 cursor_invalid`; it is not translated to
the rebuilt stream. The client reloads known public observations and obtains a
new cursor. There is no direct external Session delete route. If another
authorized operation deletes a Session, Server retains a minimal cursor
tombstone for the retention window. A valid current-generation cursor against
that closed tombstone returns `410 cursor_expired` with null
`earliestSequence` and the last safe `latestSequence`. Without a valid cursor,
the response is `session_not_found`. After purge, the old cursor is
`400 cursor_invalid`. A new logical Session always has a new Session ID.

### Resume, duplicates, ordering, and retention

A client stores a cursor only after durably processing its page. Concurrent GET
requests may arrive out of order, so the client deduplicates by
`(sessionId, sequence)`, applies events in ascending sequence, and never infers
a missing transition from a later sequence. On a gap it resumes from the last
contiguous cursor or rereads the target Input or Turn.

V1 retains every public event while its AgentSession is retained. Transcript
compaction does not compact this stream, and there is no time-based public
event compaction. If a future retained-history operation reclaims a prefix, it
persists earliestSequence in the same projection transaction as its retained
snapshot and checkpoint. A cursor before that floor returns `410
cursor_expired`; malformed, cross-Project, cross-Session, or wrong-generation
cursors return `400 cursor_invalid`. Server never silently restarts either kind
at the beginning or current head.

The route is not sourced from an in-memory event bus, SignalR hub, Runner
notification, or UI timeline. Those channels may notify a client to reread the
persisted route, but they cannot define its cursor, ordering, generation, or
payload.

## Stop, terminal fences, and unknown outcomes

`POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop` is the only direct
external control operation. It targets one canonical Turn and cannot name a
Runner, Runtime Session, dispatch attempt, or internal operation. It is distinct
from `mo session stop`, whose root Session cascade is defined by
[subagents.md](subagents.md#cascade-stop); neither route makes Session terminal.

After authorization, the first keyed request durably maps
`(callerKeyId, projectId, sessionId, turnId, Idempotency-Key)` to one per-target
stop operation before any Runner effect. Server freezes the target revision,
context generation, complete binding or explicit null binding, and deadline.
These facts remain internal and follow the canonical [operation
projection](conventions.md#canonical-sessionoperationread) and [effect
fence](conventions.md#canonical-effect-fence).

```text diagram
             +-----------------+
             | same public key |
             +--------+--------+
                      |
                      v
            +------------------+
            | same frozen Turn |
            +---------+--------+
            +---------+----------+
            v                    v
+----------------------+    +---------+
| terminal public fact |    | unknown |
+----------------------+    +----+----+
                                 |
                                 v
                     +----------------------+
                     | repeat the same POST |
                     +----------------------+
```

A terminal Turn produces a durable no-op and no Runner call. A queued Turn ends
locally as cancelled. A running Turn uses the canonical fenced stop lifecycle
and is cancelled only after Runtime confirms the stop. A changed Turn, binding,
context, or owner cannot redirect the request to replacement work.

A matching retry resolves the same mapping, snapshot, operation, and outcome.
It never rereads current binding to create a replacement deadline or effect. A
different fingerprint returns `409 idempotency_key_reused`. While the original
stop is unknown, a different key returns `409 stop_outcome_unknown` and cannot
supersede it. Response loss is recovered by repeating the same POST because no
internal-operation lookup route exists.

Execution completion and stop race through the same terminal fence. The first
terminal fact wins and emits at most one terminal public event. Before that
fact exists, an uncertain stop remains `unknown`, Session admission stays
blocked, and no automatic replay occurs. Responses and events expose only the
public Turn observation and safe reasonCode, never target, binding, deadline,
owner, lease, fence, or internal operation ID.

## Privacy boundary

This is a private-Project API, not a cross-user visibility system. PAT
authorization gates the Project, while the strict `PublicExecutionRead` and
`PublicEventPage` allowlists define external privacy. They expose canonical
IDs, public status/output/error, timestamps, event sequence, and opaque cursors.
They never expose Runner or Connection details, Runtime Session IDs, prompt or
Input content, memory, Workspace/workdir/path, raw payloads, raw Runner or
provider errors, or Runner control. Controlled diagnostics and the product
transcript remain separate surfaces.

## Ownership boundaries

- Every launch is AgentJob-owned, including a Workflow task launch. There is
  no TaskRun-owned Action path. Workflow is launch attribution, not a second
  work lifecycle or dispatch owner.
- Capacity, admission, and retryable queued state remain canonical execution
  facts. This API adds no queue and does not reinterpret a retryable block as
  terminal.
- The public projector consumes canonical result facts and applies the smaller
  external allowlist. It never exports a product transcript or internal read
  model.
- Agent history and Session timeline may consume the persisted public stream;
  they do not own ordering, retention, checkpoints, generations, or cursors.

## Non-Goals

- No Runner or Runtime selection, Workspace or prompt content, transcript, or
  Project-wide event stream.
- No generic developer platform, OAuth clients, general RBAC, cross-user
  transcript visibility, or client-owned event log.
- No direct route for internal operations, physical Runtime Sessions, provider
  effects, or arbitrary execution options.

Any added capability must enter through Agent API and the existing Connection
boundary.

## Status

The `/api/v1` External Agent API is shipped. Bearer PAT authentication,
Project grants, keyed launch/follow-up/stop writes, projection-backed
Job/Input/Turn reads, five-state observations, and resumable Session events are
part of the boundary. Web, CLI, Agent Connection, and product cascade-stop
routes remain separate adapters with their own contracts.
