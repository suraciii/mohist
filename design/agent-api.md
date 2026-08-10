---
status: wip
---

# External Agent API

## Purpose

External callers need to retry writes and resume reads across network loss without duplicating
Agent work or receiving Mohist's internal execution state. This document defines the versioned
direct API that provides that boundary for a private Project.

```text diagram
external caller -- PAT + Project grant --> /api/v1 Agent API
                                               |
                          +--------------------+--------------------+
                          |                                         |
                          v                                         v
              canonical Agent execution                 public projection
              Job / Session / Input / Turn              snapshot / event cursor
```

The API adapts existing canonical state; it does not create another execution lifecycle, queue,
Runtime Session, or client-owned event log. Authority is intentionally split:

| Concern | Authority |
|---|---|
| AgentJob, AgentSession, SessionInput, AgentTurn, admission, and recovery | [Agent execution](agent-execution.md) |
| Canonical internal read shapes and effect fences | [Conventions](conventions.md) |
| PAT identity, scopes, and Project grants | [Authentication and identity](auth.md) |
| Direct external routes, public fields, idempotency, and cursors | This document |
| Product-level root Session cascade stop | [Subagent cascade stop](subagents.md#cascade-stop) |

This contract is deliberately narrower than a general developer platform. It adds only the
credential-bound private-Project grant needed by direct callers. It does not add encryption at
rest, cross-user transcript visibility, multi-tenant policy, OAuth clients, or general RBAC. A
trusted Agent Connection is a separate adapter and cannot substitute for the direct caller's PAT.

## Boundary decisions

| Decision | Contract |
|---|---|
| Direct caller identity | Every direct API request sends Authorization: Bearer PAT. The PAT identifies the caller and is never replaced by a caller-supplied connection identity. |
| Project authorization | A PAT resolves to the minimal ExternalAgentCaller grant in [auth.md](auth.md). Its route scope and explicit Project grant, or explicit operator_all grant, must pass before any idempotency or admission work. |
| Write authentication | operator is required for launch, follow-up, and stop. |
| Read authentication | readonly or operator is required for Job, Input, Turn, and event reads. |
| Canonical ownership | The Server alone creates and updates Job, Session, Input, Turn, queue, operation, and terminal facts. The caller receives only public observations. |
| Caller retry identity | The caller supplies an Idempotency-Key; the Server normalizes the complete accepted request and computes its fingerprint. The caller never submits a hash to be trusted. |
| Public state | Every public read reports exactly one aggregate state: accepted, queued, running, terminal, or unknown, plus the component facts needed to explain it. |
| Events | A Session event stream is a Server-owned durable public projection. Canonical aggregates and their outboxes are inputs; the journal is not an internal event bus, Runner stream, transcript dump, or client-side TimelineItem sequence. |

The API has no endpoint for selecting a Runner, Runtime, workspace, physical
Runtime Session, prompt memory, model, instructions, Skills, or a provider
operation. The selected Agent and canonical Session determine those facts.

## HTTP surface

All routes are under /api/v1. Route IDs are canonical Mohist IDs, not display
names. A command returns 200 OK once its durable keyed outcome is known; this
does not mean that execution completed. The body state is authoritative.

| Method and route | Required scope | Request | Success response |
|---|---|---|---|
| POST /api/v1/projects/{projectId}/agents/{agentId}/launch | operator | Bearer PAT, Idempotency-Key, launch body | PublicExecutionRead for the unique launch mapping |
| POST /api/v1/projects/{projectId}/agent-sessions/{sessionId}/inputs | operator | Bearer PAT, Idempotency-Key, follow-up body | PublicExecutionRead for the unique Input/Turn mapping or durable rejection |
| GET /api/v1/projects/{projectId}/agent-jobs/{jobId} | readonly | Bearer PAT | PublicExecutionRead anchored to the public AgentJob projection |
| GET /api/v1/projects/{projectId}/agent-inputs/{inputId} | readonly | Bearer PAT | PublicExecutionRead anchored to the Input |
| GET /api/v1/projects/{projectId}/agent-turns/{turnId} | readonly | Bearer PAT | PublicExecutionRead anchored to the Turn |
| GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events | readonly | Bearer PAT, optional after and limit | PublicEventPage |
| POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop | operator | Bearer PAT, Idempotency-Key, empty body | PublicExecutionRead for the target Turn |

There is deliberately no generic Session, Runner, Runtime, transcript,
operation, or internal-event export route in this API version. The Job route is
a constrained public projection, not a serialized AgentJobLaunchRead. Launch
and follow-up responses give stable IDs that can be read through Job, Input,
Turn, and Session event routes.

### Write request bodies

The v1 launch and follow-up payload is intentionally small. It accepts text
only; attachments, arbitrary context references, and caller-selected execution
options are not silently accepted or ignored.

~~~json
{
  "text": "Investigate the failed deployment and report the result."
}
~~~

text is required and non-empty after validation. Unknown properties, duplicate
JSON property names, invalid JSON, and a missing or invalid Idempotency-Key fail
with 400 before admission. Text is retained as the canonical Input under the
existing controlled Session boundary, but never appears in a direct API response
or public event.

Idempotency-Key is an opaque caller string of 1 through 128 printable ASCII
characters. It is required for every write, including stop. It is a header, not
a JSON field, a trace ID, or an Input ID. On stop it is the caller-visible
operation key; it is never a Server-generated internal operation ID.

## Authentication and admission order

PAT issuance for this boundary requires an authenticated issuer and either an explicit Project
binding (`--project`) or an explicit operator-wide grant (`--scope operator --all-projects`). A
failed binding returns `403` and persists neither a Credential nor a Project grant. The grant model
and token lifecycle are authoritative in [auth.md](auth.md#externalagentcaller).

For every route, the Server applies this order:

1. Authenticate the Bearer PAT and resolve its Principal plus
   ExternalAgentCaller callerKeyId, scopes, and Project grant.
2. Authorize the required scope and selected private Project against that grant;
   for Agent, Job, Session, Input, and Turn routes, authorize the resource's
   canonical Project membership as well.
3. Validate the route, header, query, and JSON syntax without creating domain
   state.
4. Normalize the complete allowed write payload and compute the request
   fingerprint.
5. Atomically look up the idempotency mapping, then perform canonical admission
   only when no matching mapping exists.

401 unauthenticated and 403 forbidden are terminal at step 1 or 2. A selected
Project outside the PAT grant is always 403 before resource lookup. These paths
do not look up or return an idempotency mapping, create a rejection tombstone,
reserve a Job/Session/Input/Turn, write an outbox item, append a public event,
or issue a Runner/provider operation. This makes authentication and
authorization prior to both duplicate reconciliation and admission.

The direct API accepts a Bearer PAT only. Cookie-based Web sessions and trusted
Agent Connection identities remain their own entry adapters; they cannot be
presented as a direct caller key or bypass the direct route's PAT requirement.

## Normalized fingerprint and idempotency

The Server parses the accepted JSON once and creates a versioned canonical
representation. It preserves the text value exactly as a JSON string after
parsing; it does not trim, case-fold, or otherwise make two distinct prompts
equivalent. Canonical JSON property ordering and the route's canonical IDs make
the representation deterministic. The Server persists only the resulting
fingerprint with the durable request mapping; it does not expose the fingerprint
or raw request as public output.

The exact scopes are:

| Command | Idempotency scope | Normalized fingerprint input | Same key and fingerprint | Same key and different fingerprint |
|---|---|---|---|---|
| Launch | (projectId, agentId, Idempotency-Key) | contract version, launch, canonical projectId, canonical agentId, complete accepted body | Return the original canonical Job/Session/Input/Turn mapping and its current public observation | 409 idempotency_key_reused; no new canonical record, event, queue entry, outbox item, or external effect |
| Follow-up | (sessionId, Idempotency-Key) | contract version, followup, canonical sessionId, complete accepted body | Return the original Input/Turn mapping or durable rejection and its current public observation | 409 idempotency_key_reused; no new Input, Turn, queue entry, outbox item, or external effect |
| Stop | (turnId, Idempotency-Key) | contract version, stop, canonical turnId, empty body | Return the original target Turn observation | 409 idempotency_key_reused; no new stop operation or external effect |

For stop, `(turnId, Idempotency-Key)` is the caller-visible route scope. Its
durable mapping additionally binds callerKeyId, canonical projectId, sessionId,
and turnId so one caller cannot look up or replay another caller's public key.

For a follow-up, the Server first resolves the Session and derives its Project
and Agent from the canonical Session. A caller cannot put a Project or Agent in
the body, choose a different Agent under the same Session key, or influence the
fingerprint with a client-declared derived value.

The mapping is durable before a successful command response. The first launch
creates at most one canonical Job/Session/Input/Turn group; a response-loss
retry with the same scope and payload returns that same group. A follow-up
creates at most one canonical Input/Turn pair. A definitive admission rejection
is also durable under the same key so capacity recovery, reconnects, and retries
cannot change a rejected request into a newly accepted one.

The canonical mapping is stable. Its public status, timestamps, output, error,
and event sequence can advance as the Server learns more facts, but retrying a
matching request never mints different IDs or another execution.

## Public execution projection

PublicExecutionRead is the only execution-shaped object returned by command and
resource-read routes. It is a strict allowlist, not a serialized
AgentJobLaunchRead, AgentSessionRead, SessionInputRead, TurnResultRead, or
SessionOperationRead.

~~~json
{
  "projectId": "proj_123",
  "agentId": "agent_123",
  "jobId": "job_123",
  "sessionId": "session_123",
  "inputId": "input_123",
  "turnId": "turn_123",
  "status": "queued",
  "jobStatus": "queued",
  "sessionActivity": "active",
  "admission": "blocked",
  "inputStatus": "accepted",
  "turnStatus": "queued",
  "outcome": null,
  "reasonCode": null,
  "output": null,
  "error": null,
  "acceptedAt": "2026-08-09T10:15:30Z",
  "queuedAt": "2026-08-09T10:15:31Z",
  "startedAt": null,
  "terminalAt": null,
  "observedAt": "2026-08-09T10:15:31Z",
  "sequence": 18
}
~~~

Every listed key is present. IDs and timestamps can be null only where the
canonical fact does not exist: for example, a launch rejected before Session
acceptance has a jobId but no live sessionId, inputId, or turnId. A prepared
launch can likewise have a jobId with null live IDs while Session acceptance is
still pending. sequence is null only when no Session public event could exist.
No response contains an unlisted execution property.

| Field | Values and meaning |
|---|---|
| projectId, agentId, jobId, sessionId, inputId, turnId | Canonical stable IDs. jobId is null for a follow-up; Input and Turn IDs are null for a durable rejection that intentionally did not create live records. |
| status | The five-state aggregate: accepted, queued, running, terminal, or unknown. |
| jobStatus | preparing, queued, running, terminal, unknown, or null. It is a public component fact, not a sixth aggregate state. |
| sessionActivity | idle, active, unknown, or null. |
| admission | ready, blocked, or null. It is distinct from execution outcome. |
| inputStatus | accepted, rejected, unknown, or null. |
| turnStatus | queued, running, outcome_pending, terminal, unknown, or null. |
| outcome | completed, rejected, failed, cancelled, blocked, or null. |
| reasonCode | Null or one stable safe public reason code. It explains a public status or Session boundary without carrying internal detail. |
| output | Null or { "text": "..." } containing only persisted public final output. It is never a transcript or raw provider response. |
| error | Null or { "code": "stable_public_code", "message": "safe public explanation" }. It never carries a stack trace, provider error, path, or opaque internal identity. |
| acceptedAt, queuedAt, startedAt, terminalAt, observedAt | RFC 3339 UTC timestamps for public canonical facts. observedAt is always present. |
| sequence | The latest persisted public Session event sequence that reflects this observation, or null before a Session exists. |

The allowlist excludes, without exception: runtimeSessionId, Runner IDs, runtime
names, binding epochs, connection IDs, leases, fences, operation IDs, attempt
IDs, dispatch/retry details, prompt or input text, Instructions, memory, tool
state, workspace/workdir/path, attachments, raw payloads, raw transcript facts,
and raw provider or Runner errors. A safe public reasonCode or error may state
queue_full, context_reset, or stop_outcome_unknown; it cannot include the
private cause detail.

### Public AgentJob read

GET /api/v1/projects/{projectId}/agent-jobs/{jobId} returns the same strict
PublicExecutionRead, anchored to the canonical Job's durable public projection.
It never returns AgentJobLaunchRead or another raw Job shape. A prepared Job
whose Session is not yet accepted returns 200 with its jobId,
status=accepted, jobStatus=preparing, and null sessionId/inputId/turnId. A
durable Session rejection returns 200 with that same jobId, status=terminal,
outcome=rejected, safe error/reasonCode, and null live IDs. After acceptance,
the same Job read exposes its public Session/Input/Turn references and later
public status, output, or error as they become projected.

The same authorization order applies: a PAT without the selected Project grant
receives 403 before Job lookup; an authorized Project whose Job is absent or
does not belong to it receives 404 job_not_found. If a launch response was lost
before the caller learned jobId, it repeats the launch with the same
Idempotency-Key and receives the same Job anchor or projection_lag; it never
creates a replacement Job. This route is the minimal public status recovery
path, not a generic Session or operation lookup.

## Projection consistency and recovery

AgentJob and AgentSession do not share a cross-aggregate transaction. Their
canonical records and durable outboxes remain the source of truth described by
[agent-execution.md](agent-execution.md). The direct API therefore must not
claim that a PublicExecutionRead or public event is atomically committed with a
combined Job/Session/Input/Turn write.

Instead, the Server owns one durable public projection per target Session (or a
launch target before a Session exists). A launch target is permanently anchored
by jobId, so its Job projection remains addressable before and after Session
acceptance. Its inputs are canonical aggregate records plus their durable outbox
facts. A projector normalizes those inputs and persists, in **one projection
transaction**, all of the following:

1. the allowlisted PublicExecutionRead snapshot for every affected public
   target;
2. the corresponding public Session event journal entries and sequences; and
3. the source checkpoint/watermark proving which durable outbox facts the
   snapshot and journal include.

PublicExecutionRead and PublicEventPage are read only from this projection.
They are therefore mutually consistent at one recorded projection checkpoint,
but are intentionally eventually consistent with the independent canonical
aggregates. They never read a partial Job/Session combination directly or turn
an internal outbox delivery into an external event payload.

For a prepared launch, the projector can publish a Job-anchored accepted state
with null live IDs after the canonical Job prepare fact. It waits for the
matching Session acceptance/rejection fact before it publishes a joined
Job/Session/Input/Turn mapping, then updates that same Job anchor with the
public references. For a follow-up it waits for the matching Session Input/Turn
fact. If an authorized route knows a required source watermark is ahead of the
stored projection checkpoint, it returns `503 projection_lag` and the caller
retries the same key or read; it must not return a stale state as current.
Projection lag is a transport/reconciliation condition, not the public
five-state `unknown`.

`unknown` is emitted only when the projector has consumed the required durable
facts and those facts say that acceptance, dispatch, binding, stop, or outcome
cannot yet be confirmed. A confirmed canonical terminal rejection needs no Turn
fence. A Turn terminal projection stores the canonical terminal fence/revision
internally and can become terminal only after the current terminal fact passes
that fence. Later stale outbox facts, delayed Runner results, or replayed
projector input cannot move that target back to a non-terminal public state.

The projection checkpoint, snapshot, event entries, event identity, and next
sequence are committed together. A crash before that transaction commits leaves
no partial snapshot, sequence, or checkpoint; restart replays the same durable
outbox input. A crash after commit resumes after the checkpoint and cannot emit
a second public sequence for the same normalized source transition. This is
projection recovery, not replay of a Runner, launch, follow-up, or stop effect.

## Five-state mapping and precedence

status is a projection over canonical facts. It never replaces the underlying
Job, Session, Input, or Turn state. The component fields above stay visible so
callers do not lose blocked, rejected, or outcome_pending facts by seeing only
one label.

| Aggregate status | Canonical basis | Required component facts |
|---|---|---|
| accepted | A Job has been durably prepared, or an Input is durably accepted, but no current Turn or Job is yet queued, running, outcome_pending, terminal, or unknown. | Known Job/Input/Session IDs as applicable; inputStatus=accepted when an Input exists. |
| queued | The current Job or target Turn is canonically queued, with no unresolved fact or terminal fence. | jobStatus=queued or turnStatus=queued; a retryable dispatch block remains visible as admission=blocked and public error, but does not become terminal. |
| running | The current Job or target Turn is running, or the Turn is outcome_pending, with no unresolved fact or terminal fence. | turnStatus=running or outcome_pending; outcome_pending always has admission=blocked and never implies a final output. |
| terminal | A durable input rejection, Job terminal outcome, or Turn terminal outcome exists. | inputStatus=rejected with outcome=rejected, or jobStatus/turnStatus=terminal with outcome=completed, failed, cancelled, or blocked. |
| unknown | The Server cannot confirm a Job, Session, Input, Turn, dispatch, binding, stop, or outcome fact and no fenced terminal fact resolves the target. | At least one applicable jobStatus, sessionActivity, inputStatus, or turnStatus is unknown; admission=blocked whenever a Session exists. |

An Input or Turn read is anchored to its requested canonical record. A terminal
target remains terminal even when the enclosing Session is active because a
later Turn is queued or running. Conversely, an active Session does not turn a
terminal Job or Turn into running. sessionActivity is context, not a replacement
for the requested Input/Turn outcome.

The precedence is fixed:

1. A durable terminal fact protected by the target Turn's terminal fence wins.
   Late Runner, stop, or event-bus observations cannot move that Turn back to a
   non-terminal state or replace its output/error.
2. A durable rejection is terminal with outcome=rejected, even though it may
   have no live Input or Turn ID.
3. Without a terminal fact, any unresolved canonical acceptance, dispatch,
   binding, stop, or outcome fact is unknown.
4. outcome_pending is running, never terminal; it is shown explicitly in
   turnStatus and blocks admission.
5. A retryable dispatch blocked state remains queued with admission=blocked.
   Only a terminal Turn or Job outcome of blocked becomes terminal.
6. If none of the above applies, a running fact wins over queued, and queued
   wins over accepted.

unknown and outcome_pending never authorize automatic replay. The Server queries
or reconciles an existing durable operation only where the canonical lifecycle
already permits that operation. It never creates a new Job, Input, Turn,
dispatch attempt, or stop simply because a public client reconnects, polls, or
repeats a different key.

## Public errors

Error responses use one safe envelope:

~~~json
{
  "error": {
    "code": "cursor_invalid",
    "message": "The cursor is not valid for this Session."
  }
}
~~~

The only extension is 410 cursor_expired, whose sequence bounds are explicitly
safe public fields:

~~~json
{
  "error": {
    "code": "cursor_expired",
    "message": "The cursor is older than the retained Session history."
  },
  "earliestSequence": 120,
  "latestSequence": 183
}
~~~

| HTTP | Code | Meaning and side-effect rule |
|---|---|---|
| 400 | invalid_request | Invalid JSON, unknown field, invalid route/query value, or invalid body. No admission occurs. |
| 400 | idempotency_key_required / idempotency_key_invalid | A write lacks a usable key. No request mapping or domain record is created. |
| 400 | cursor_invalid | The opaque cursor is malformed, tampered with, bound to another Project, Session, or stream generation, or cannot be decoded. No fallback or event read is attempted. |
| 401 | unauthenticated | The Bearer PAT is absent, invalid, expired, or revoked. The response includes WWW-Authenticate: Bearer; no idempotency or admission effect occurs. |
| 403 | forbidden | The authenticated caller lacks route scope or the ExternalAgentCaller grant for the selected Project. A grant failure precedes resource lookup, idempotency, and admission, and has zero effects. |
| 404 | project_not_found, agent_not_found, job_not_found, session_not_found, input_not_found, or turn_not_found | Only after the caller's Project grant passes, the requested canonical resource is not available in that Project. 404 is not the public execution state unknown. |
| 409 | idempotency_key_reused | The same durable scope/key was supplied with a different normalized payload. The conflict is stable and creates no new effect. |
| 409 | stop_outcome_unknown | A different stop key attempts to supersede a stop whose fenced outcome is still unknown. The caller must read the Turn; no new stop is issued. |
| 410 | cursor_expired | The cursor was valid but falls before the retained public event floor. The response also includes safe earliestSequence and latestSequence values. The caller reloads current Input/Turn observations before starting at a new retained position. |
| 503 | projection_lag | The canonical request/resource is known, but its required durable public projection checkpoint has not caught up. No new admission or effect occurs; retry the same key or read. |

Canonical admission rejection is not hidden as an HTTP transport failure. A
well-formed keyed launch or follow-up that receives a durable rejection returns
200 with status=terminal, outcome=rejected, and a safe public error. That is the
only form that makes response-loss replay return the same durable decision
without inventing an Input or Turn later.

## Persisted public Session events

### Scope and shape

GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events reads one
Session's durable public projection. It never reads a Project-wide mixed stream.
after is an optional opaque cursor and limit is optional, with a default of 100
and a maximum of 100. The resume rule is exclusively **after**: the page
contains only events whose sequence is greater than the position encoded by
after. There is no implicit inclusive replay mode.

~~~json
{
  "sessionId": "session_123",
  "events": [
    {
      "sequence": 18,
      "cursor": "opaque-session-cursor",
      "type": "turn.queued",
      "occurredAt": "2026-08-09T10:15:31Z",
      "execution": {
        "projectId": "proj_123",
        "agentId": "agent_123",
        "jobId": "job_123",
        "sessionId": "session_123",
        "inputId": "input_123",
        "turnId": "turn_123",
        "status": "queued",
        "jobStatus": "queued",
        "sessionActivity": "active",
        "admission": "blocked",
        "inputStatus": "accepted",
        "turnStatus": "queued",
        "outcome": null,
        "reasonCode": null,
        "output": null,
        "error": null,
        "acceptedAt": "2026-08-09T10:15:30Z",
        "queuedAt": "2026-08-09T10:15:31Z",
        "startedAt": null,
        "terminalAt": null,
        "observedAt": "2026-08-09T10:15:31Z",
        "sequence": 18
      }
    }
  ],
  "nextCursor": "opaque-session-cursor",
  "highWaterSequence": 18
}
~~~

The execution vocabulary is finite: input.accepted, input.rejected,
turn.queued, turn.running, turn.outcome_pending, turn.terminal, and
session.unknown. These events carry execution, which is exactly
PublicExecutionRead; there is no raw event data.

session.context_reset is also a public event. It is emitted only from a durable
canonical ContextBoundary/Session reset fact. The projector appends it with the
affected public snapshot and source checkpoint in one projection transaction.
It carries the following distinct, smaller allowlisted session payload instead
of execution:

~~~json
{
  "sequence": 19,
  "cursor": "opaque-session-cursor",
  "type": "session.context_reset",
  "occurredAt": "2026-08-09T10:16:01Z",
  "session": {
    "projectId": "proj_123",
    "agentId": "agent_123",
    "sessionId": "session_123",
    "sessionActivity": "idle",
    "admission": "ready",
    "reasonCode": "context_reset"
  }
}
~~~

For session.context_reset, no jobId, inputId, turnId, output, error, prompt,
memory, runtime, path, raw payload, or operation/binding data is present. The
outer sequence and occurredAt are its public ordering and timestamp facts; its
sessionActivity and admission are the only public Session-status fields.

An event cursor is the exclusive continuation position immediately after that
event; nextCursor equals the last event cursor in a non-empty page. The
projector appends a public event only in the same **projection transaction**
that persists the corresponding PublicExecutionRead snapshot and source
checkpoint. Each Session's sequence is a strictly increasing positive integer
across all its stream generations. It never reuses or renumbers a sequence, and
an event page is sorted ascending by sequence. The cursor is opaque,
tamper-evident, and bound to this Project, Session, stream generation, and
exclusive sequence position. Clients treat it as data, not as a parseable ID.

### Stream generation and lifecycle

The first committed public projection for a Session creates stream generation
one. Generation is stable across normal projector restart, crash recovery,
outbox replay, and ordinary projection checkpoint advancement. A projection
rebuild or restore never mutates that live journal in place: it builds a new
generation from durable canonical/outbox inputs, persists its snapshot and
checkpoint, then atomically makes that generation current. It preserves the
Session's next global sequence allocator, so a sequence is never reused even
when the active generation changes.

An old-generation cursor is a wrong stream/generation cursor and returns 400
cursor_invalid. It is not silently translated into the rebuilt stream. A client
then reloads its known public Input/Turn observations and obtains a new cursor
from the current generation. This makes a rebuild/restore explicit without
exposing its internal cause.

There is no direct external Session delete route. When another authorized
control-plane action deletes a Session, Server closes its public stream and
retains a minimal cursor tombstone for the cursor-retention window. A valid
current-generation cursor against that closed tombstone returns 410
cursor_expired with earliestSequence=null and the last safe latestSequence; a
request without a valid cursor returns session_not_found. After physical stream
purge removes the tombstone, a cursor cannot be recognized and returns 400
cursor_invalid. A new logical Session always has a new SessionId and cannot
reuse a deleted stream.

### Resume, duplicates, ordering, and retention

The response's nextCursor is positioned after the last returned event. For an
empty page it is positioned at the page's highWaterSequence. A client stores
that cursor only after it durably processes the page. Retrying a GET can return
the same page; concurrent page requests can arrive out of order. The client
deduplicates by (sessionId, sequence), applies events in ascending sequence, and
does not infer a missing transition from a later sequence. When it observes a
gap, it resumes from its last contiguous cursor or rereads the target Input or
Turn.

V1 retains every public event while its AgentSession is retained. Ordinary
transcript compaction does not compact this public event stream. There is no
time-based public event compaction in v1. If a future retained-history operation
reclaims a public prefix, it persists the current generation's earliestSequence
floor in the same projection transaction as its retained snapshot/checkpoint.
A cursor whose valid current-generation after sequence is earlier than that
floor returns 410 cursor_expired; a malformed, cross-Project, cross-Session, or
wrong-generation cursor returns 400 cursor_invalid. Server never silently
restarts either kind at the beginning or current head.

The Server does not source this route from an in-memory event bus, SignalR hub,
Runner notification, or UI timeline. Those channels can be delayed, duplicated,
or absent. They may notify a client to reread this persisted route, but cannot
define its cursor, ordering, generation, or payload.

## Stop, terminal fences, and unknown outcomes

`POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop` is the only direct external control
operation. It targets one canonical Turn and cannot name a Runner, Runtime Session, dispatch
attempt, or internal operation. This adapter boundary is distinct from the product command
`mo session stop`, whose root Session cascade is defined by
[subagents.md](subagents.md#cascade-stop); neither route changes Session into a terminal entity.

After PAT scope and Project authorization, the first keyed request durably maps
`(callerKeyId, projectId, sessionId, turnId, Idempotency-Key)` to one canonical per-target stop
operation before any Runner effect. The Server, not the caller, freezes the target revision,
context generation, complete binding or explicit null binding, and deadline. These facts remain
internal and follow the canonical [operation projection](conventions.md#canonical-sessionoperationread)
and [effect fence](conventions.md#canonical-effect-fence).

```text diagram
same public key --> same frozen Turn --> terminal public fact
                         |
                         +------------> unknown
                                          |
                                          +--> repeat the same POST
```

A Turn already terminal at the first request produces a durable no-op observation and no Runner
call. A queued Turn ends locally without contacting Runtime and is recorded cancelled. A running Turn uses the canonical
fenced stop lifecycle; a changed Turn, binding, context, or owner cannot redirect the request to
replacement work.

A matching retry resolves the same mapping, snapshot, operation, and outcome. It never rereads the
current binding to create a replacement deadline or effect. Reusing the key with a different
fingerprint returns `409 idempotency_key_reused`. While the original stop is `unknown`, a different
key returns `409 stop_outcome_unknown`; it cannot supersede or replay the unresolved effect.
Response loss is recovered by repeating the same POST with the same key because this API exposes no
internal-operation lookup route.

Execution completion and stop race through the same terminal fence. Whichever terminal fact wins
is returned and emits at most one terminal public event; a late result cannot replace its outcome,
output, error, or sequence. Before a fenced terminal fact exists, an uncertain stop remains
`unknown`, Session admission stays blocked, and no automatic replay occurs. Responses and events
expose only the public Turn observation and safe `reasonCode`, never the frozen target, binding,
deadline, owner, lease, fence, or internal operation ID.

## Privacy boundary

This is a private-project API, not a cross-user visibility system.
Authentication identifies the direct caller and authorization gates the private
Project, but this issue does not add a transcript visibility matrix, secret
re-encryption, or user-to-user policy.

The absence of that broader policy is not permission to serialize the Server
read model. The strict PublicExecutionRead and PublicEventPage allowlists are
the external privacy boundary. They expose only canonical IDs, public
status/output/error, timestamps, event sequence, and opaque continuation
cursors. They never expose runner or connection details, Runtime Session IDs,
prompt/input content, memory, workspace/workdir/path, raw payload, or runner
control. Controlled internal diagnostics and the existing product transcript
remain separate surfaces with their own contracts.

## Ownership boundaries

The external API composes existing owners rather than making route shape decide domain ownership:

- A direct launch is AgentJob-owned. A Workflow `mohist/agent` Action remains TaskRun-owned and
  does not create or own an AgentJob. They share public vocabulary, not a work lifecycle or dispatch
  owner.
- Capacity, admission, and retryable queued state remain canonical execution facts. This API cannot
  add another queue or reinterpret a retryable block as a terminal outcome.
- The public result projector consumes canonical result facts but applies this document's smaller
  external allowlist. It never exports a product transcript or internal read model.
- Agent history and Session timeline may consume the persisted public stream. They do not own its
  ordering, retention, checkpoint, generation, or cursor rules.

## Status

Frontmatter remains `wip` because this document specifies target behavior, not a shipped route
surface. The current Server does not yet provide the PAT `ExternalAgentCaller` Project grant,
Server-computed external idempotency mapping, public AgentJob read, durable checkpointed public
projection, generation-aware cursor stream, or public-key-to-canonical-operation stop mapping and
frozen target defined here.

The public contract remains deliberately narrow. Existing Web,
CLI, Agent Connection, canonical Session, and product cascade-stop routes are not evidence that the
direct `/api/v1` contract is implemented. No compatibility promise or source implementation is
implied by this target specification.
