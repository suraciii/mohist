---
status: wip
---

# External Agent API

## Purpose

This document defines the versioned direct API for an external caller that wants
to launch a Mohist Agent, add an input to an existing Session, read a result, or
resume public progress after a disconnect. It is the authoritative contract for
[#387](https://github.com/suraciii/mohist/issues/387).

The API is a Server boundary over the existing canonical AgentJob, AgentSession,
SessionInput, and AgentTurn model. It does not create a second execution
lifecycle, queue, runtime-facing Session, or client-owned event log. Lifecycle,
admission, recovery, and fences remain defined by
[agent-execution.md](agent-execution.md). Internal read shapes remain defined
by [conventions.md](conventions.md).

The deployment in scope is a private Project. This contract deliberately does
not add encryption-at-rest, cross-user transcript visibility, multi-tenant
policy, OAuth clients, or a general multi-user/RBAC permission model. It adds
only the credential-bound private-Project grant required for this direct
boundary. Every direct caller still authenticates with a PAT carried as a
Bearer caller key; a trusted Agent Connection is not a substitute for that
authentication boundary.

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

POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop is the only
external control operation. It targets a canonical Turn; it cannot name a
Runner, Runtime Session, dispatch attempt, or internal operation.

After PAT scope/Project grant authorization, the first keyed request reads the
canonical Session and persists one durable ExternalStopOperation before a
Runner call. It freezes:

```text
ExternalStopOperation {
  publicKeyScope = (callerKeyId, projectId, sessionId, turnId, Idempotency-Key)
  turnId
  fingerprint
  expectedRevision
  expectedContextGeneration
  expectedBinding = complete BindingTuple | explicit null
  deadline
  internalOperationId
  outcome = pending | terminal | unknown
}
```

The caller-visible operation key is Idempotency-Key, never internalOperationId.
The first authorized request persists exactly one publicKeyScope-to-
internalOperationId mapping before it freezes any snapshot or calls a Runner.
All snapshot values are Server-read facts. The caller cannot provide or alter
revision, context generation, binding, deadline, or internalOperationId. For a
non-terminal Turn, internalOperationId names the existing canonical
SessionOperationRead(kind=stop) and its complete fence lifecycle. For a Turn
already terminal at first request, it names the durable no-op terminal
observation; no Runner stop is issued. This is an API idempotency boundary over
the existing stop lifecycle, not a second stop state machine.

A matching retry first resolves that same publicKeyScope, then reads the same
ExternalStopOperation snapshot and durable outcome. It never rereads current
binding to create a replacement operation, deadline, or effect. A different
fingerprint under the same public key returns 409 idempotency_key_reused. If the
original stop is unknown, a different key returns 409 stop_outcome_unknown until
canonical reconciliation resolves that original record; the new key cannot
target a rebinding or produce a second stop.

A queued Turn can become canonically cancelled. A running Turn invokes the
existing fenced stop lifecycle from [agent-execution.md](agent-execution.md)
only when the frozen expectedRevision, expectedContextGeneration, and complete
expectedBinding still match. If a rebind, reset, or stale owner makes that
snapshot invalid before or after an external call, the original operation stays
unknown with a safe reasonCode; it never redirects the stop to the new binding.
Response loss first resolves the same publicKeyScope to the same
internalOperationId, then queries that target attempt. The caller queries or
replays the outcome by repeating this same POST with the same Idempotency-Key;
there is no public internal-operation lookup route. No new
ExternalStopOperation, Turn, dispatch attempt, or Runner call is created
automatically.

A terminal commit is fenced against the complete canonical target. Once it
persists, the projector records one terminal public event for that Turn and
rejects late non-terminal projection updates. If execution completion wins a
race with stop, that terminal execution result is returned. If stop wins, the
terminal cancelled result is returned. A late response from either side cannot
replace the terminal outcome, output, error, or sequence.

If a stop response or provider result is uncertain before a fenced terminal fact
exists, the Turn is unknown and admission remains blocked. Retrying the same key
returns the same frozen operation/outcome; the Server does not automatically
replay it. The external response and public events expose only the public Turn
observation and safe reasonCode, never the frozen snapshot, fence, binding,
deadline, owner, lease, or internalOperationId.

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

## Prerequisites, consumer, and implementation slices

This specification remains tracked by #387; these slices do not create new
issues or a parallel milestone.

### Workflow Action ownership

`mohist/agent` Workflow Action remains TaskRun-owned. It does not create or own
an AgentJob, and its dispatch ownership must not be made identical to the direct
API launch path. Direct external launch remains AgentJob-owned. #376 is a
prerequisite for stable Action result/status/Agent/Session/Input/Turn IDs and
shared public vocabulary, not for a shared Job lifecycle or dispatch owner.

| Item | State on 2026-08-09 | Required relationship |
|---|---|---|
| [#376](https://github.com/suraciii/mohist/issues/376) reusable mohist/agent Workflow Action | Open | It supplies stable Action result/status/IDs and shared public terms. Workflow stays TaskRun-owned with no AgentJob; direct API launch stays AgentJob-owned. |
| [#382](https://github.com/suraciii/mohist/issues/382) capacity and queued state | Closed | The API reuses its canonical capacity/admission facts; it must not add a second queue or reinterpret retryable blocking as terminal. |
| [#384](https://github.com/suraciii/mohist/issues/384) default public result projection | Closed | The API may reuse its public result facts, but must apply this document's smaller external allowlist rather than export a transcript or UI projection. |
| [#385](https://github.com/suraciii/mohist/issues/385) Agent history and Session timeline | Open | Consumer only: its UI/history experience consumes #387's persisted public stream. It neither owns nor blocks #387 slice 3 retention, ordering, checkpoints, or stream generations. |

1. **Bearer admission gate**: deliver explicitly project-bound PAT issuance and route authorization so a caller can safely reach only its private Projects before any idempotency or admission work.
2. **Keyed AgentJob launch and Session follow-up**: deliver response-loss-safe launch/follow-up plus the minimal public Job, Input, and Turn observations using Server-computed normalized fingerprints and #382 admission facts, while #376 contributes vocabulary only.
3. **Durable public projection**: #387 owns allowlisted result/error snapshots, public-journal retention and ordering, checkpoints, stream generations, and context-reset events, with #384 result facts as input and #385 only as a later UI consumer.
4. **Fenced public stop and recovery**: deliver public-key-to-canonical-operation mapping and frozen stop snapshots so terminal or unknown outcomes cannot be replaced or replayed automatically.

## Acceptance criteria

- A direct external API PAT is issued only after issuer authentication and an
  explicit `--project` binding or `--scope operator --all-projects` binding;
  a failed binding returns 403 and persists no Credential or ProjectGrant.
- A missing, invalid, expired, revoked, or out-of-scope Bearer caller receives
  401 or 403, and a PAT whose ExternalAgentCaller grant excludes the selected
  Project always receives 403 before resource lookup, idempotency, admission,
  rejection tombstone, canonical record, public event, outbox entry, or Runner
  effect exists.
- A launch with one (projectId, agentId, Idempotency-Key) creates at most one
  canonical Job/Session/Input/Turn mapping. A matching retry returns those same
  IDs; a changed normalized payload always returns idempotency_key_reused and
  has zero new effect.
- A known Job has a readonly public projection: a pre-acceptance Job returns
  accepted/preparing with null live IDs, a rejected Job returns its durable
  terminal public result with null live IDs, and an accepted Job later exposes
  its public Session/Input/Turn references; wrong grant is 403 and missing or
  wrong-Project Job is 404 job_not_found.
- A follow-up with one (sessionId, Idempotency-Key) creates at most one
  Input/Turn mapping or one durable rejection. Project and Agent are derived
  from the canonical Session, not trusted from caller input.
- Input and Turn reads expose every aggregate state and the component facts that
  distinguish rejected, retryably blocked, terminally blocked, outcome-pending,
  and unknown without inventing a second lifecycle.
- A joined public read/event is internally consistent at one durable projection
  checkpoint, while Job/Session remain independently committed canonical
  aggregates. Required source lag returns projection_lag; only consumed durable
  unresolved facts become unknown.
- Every direct response and persisted public event passes the strict allowlist;
  no response or event serializes a Runtime Session ID, runner/connection data,
  prompt/input, memory, workdir/path, raw payload, or Runner control field.
- An event cursor resumes exclusively after its position; sequences are
  monotonic per Session across stream generations, pages are ordered,
  duplicate/out-of-order delivery is safe to deduplicate, wrong-generation
  cursors return cursor_invalid, and valid retained-history cursors before the
  earliest floor return cursor_expired without an implicit reset. A durable
  context reset emits only the allowlisted session.context_reset payload.
- A stop race emits at most one terminal public fact for a Turn. Terminal state
  cannot be overwritten by a late stop/execution result; the first keyed stop
  maps the caller/project/session/turn public key to one hidden canonical
  operation, freezes its revision/context/binding/deadline, and lets only that
  same key query/replay its outcome; unresolved stop and execution facts remain
  unknown and never cause automatic replay.

## Deterministic verification matrix

| Scenario | Controlled seam | Required assertion |
|---|---|---|
| PAT issuance and route grant | Fake issuer/project binder plus ExternalAgentCaller resolver and recording stores | Repeated --project persists only explicit IDs; --scope operator --all-projects persists operator_all; invalid binding is 403 with no Credential/ProjectGrant, while a route grant miss is 403 before resource lookup, idempotency, durable writes, public events, outbox records, and Runner calls. |
| First launch, response-loss retry, concurrent same-key launch | Deterministic transaction barrier and fake Job/Session stores | One Job/Session/Input/Turn mapping only; all matching callers receive the same IDs. |
| Public AgentJob read | Shared durable projection fixture for prepared, rejected, and accepted Jobs | The Job route returns only PublicExecutionRead; pending acceptance has jobId with null live IDs, rejection preserves a terminal public result, later acceptance adds stable references, and response loss returns the same Job through keyed launch replay. |
| Launch key reused with changed text or unknown JSON property | Canonical JSON normalizer and recording stores | Stable 409 idempotency_key_reused or 400 invalid_request; no new record/effect. |
| Follow-up key scope | Two Sessions for one Agent and two Agents in one Project | Same key is isolated by Session; the request never accepts caller-provided derived Project/Agent values. |
| Rejected, retryably blocked, outcome-pending, terminal blocked, completed, and unknown | Fake canonical state rows | The five-state aggregate and all component fields follow the precedence table exactly. |
| Projection leakage | Sentinel values in every internal binding, operation, workspace, prompt, memory, path, runner, and raw-error field | JSON responses/events contain only allowlisted keys and never contain any sentinel. |
| Cross-aggregate projection convergence | Shared in-memory SQLite database, new DbContext instances, independent Job/Session outbox rows, and a new projector instance | Public snapshot/event/checkpoint commit together at one source watermark; before the required watermark the route returns projection_lag rather than a partial or stale joined state. |
| Projector crash before and after checkpoint | Shared in-memory SQLite database with an injected crash barrier before or after the projection transaction | Restarted projector replays uncheckpointed outbox input, skips checkpointed input, emits no duplicate sequence, and never replays a Runner/launch/follow-up/stop effect. |
| Restarted continuation | New DbContext and projector instance over the same shared database | Current generation, next sequence, high-water, cursor continuation, and exclusive after semantics survive restart unchanged. |
| Projection rebuild and stream rotation | Durable canonical/outbox fixture plus explicit projection rebuild/restore command | New generation becomes current without sequence reuse; old-generation cursor is cursor_invalid; current-generation stale retained cursor is cursor_expired. |
| Context reset projection | Durable ContextBoundary/reset outbox fixture | Exactly one session.context_reset event contains only public IDs, session status, safe reasonCode, timestamp, and sequence; it leaks no runtime/path/prompt/memory/raw data. |
| Ordered continuation and GET retry | Persisted in-memory public event journal with injected sequence allocator | Pages are ascending, after is exclusive, nextCursor resumes after the last event, and duplicate pages deduplicate by (sessionId, sequence). |
| Concurrent out-of-order pages and gap | Deterministic client reducer plus reordered page delivery | Client applies only ascending contiguous sequence and rereads/resumes rather than inventing a missing transition. |
| Tampered, cross-session, wrong-generation, and compacted cursors | Cursor codec, current generation, and injected retained floor | 400 cursor_invalid for malformed/scope/generation mismatch; 410 cursor_expired with safe sequence bounds for a valid current-generation stale cursor; neither causes an implicit restart. |
| Stop versus completion, rebind, and response loss | Fenced fake Runtime with barriers, persisted publicKeyScope-to-internalOperationId mapping, and injected TimeProvider | First public key fixes one canonical operation plus revision/context/binding/deadline; matching key reads/replays that outcome without exposing the internal ID, changed fingerprint conflicts, one terminal event/output/error survives, and stale/rebind/response loss remain unknown with no replay. |

Every durability scenario uses a shared in-memory SQLite database across newly
constructed DbContext/projector instances, deterministic fakes, and injected
TimeProvider. It uses no real filesystem, network, Runner, provider, or wall
clock.

## Implementation gap

This is target behavior only. The current route surface does not yet provide the
PAT ExternalAgentCaller Project grant, Server-computed external idempotency
mapping, public AgentJob read, durable checkpointed public projection,
generation-aware cursor stream, or public-key-to-canonical-operation stop
mapping/frozen snapshot defined here. Those gaps remain tracked by
[#387](https://github.com/suraciii/mohist/issues/387) and its four slices above.
No source implementation is implied by this specification change.
