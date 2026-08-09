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
policy, OAuth clients, or a new permission model. It still requires every
direct caller to authenticate with a PAT carried as a Bearer caller key; a
trusted Agent Connection is not a substitute for that authentication boundary.

## Boundary decisions

| Decision | Contract |
|---|---|
| Direct caller identity | Every direct API request sends Authorization: Bearer PAT. The PAT identifies the caller and is never replaced by a caller-supplied connection identity. |
| Project authorization | A valid principal must be authorized for the selected private Project and route scope before any idempotency or admission work. This reuses [auth.md](auth.md); it does not introduce cross-user visibility policy. |
| Write authentication | operator is required for launch, follow-up, and stop. |
| Read authentication | readonly or operator is required for Input, Turn, and event reads. |
| Canonical ownership | The Server alone creates and updates Job, Session, Input, Turn, queue, operation, and terminal facts. The caller receives only public observations. |
| Caller retry identity | The caller supplies an Idempotency-Key; the Server normalizes the complete accepted request and computes its fingerprint. The caller never submits a hash to be trusted. |
| Public state | Every public read reports exactly one aggregate state: accepted, queued, running, terminal, or unknown, plus the component facts needed to explain it. |
| Events | A Session event stream is a Server-owned persisted public projection. It is not an internal event bus, Runner stream, transcript dump, or client-side TimelineItem sequence. |

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
| GET /api/v1/projects/{projectId}/agent-inputs/{inputId} | readonly | Bearer PAT | PublicExecutionRead anchored to the Input |
| GET /api/v1/projects/{projectId}/agent-turns/{turnId} | readonly | Bearer PAT | PublicExecutionRead anchored to the Turn |
| GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events | readonly | Bearer PAT, optional after and limit | PublicEventPage |
| POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop | operator | Bearer PAT, Idempotency-Key, empty body | PublicExecutionRead for the target Turn |

There is deliberately no generic Job, Session, Runner, Runtime, transcript,
operation, or internal-event export route in this API version. Launch and
follow-up responses give the stable IDs that can be read through Input, Turn,
and Session event routes.

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
a JSON field, a trace ID, an Input ID, or a Server-generated operation ID.

## Authentication and admission order

For every route, the Server applies this order:

1. Authenticate the Bearer PAT and resolve its Principal.
2. Authorize the required scope and the selected private Project; for Agent,
   Session, Input, and Turn routes, authorize the resource's canonical Project
   membership as well.
3. Validate the route, header, query, and JSON syntax without creating domain
   state.
4. Normalize the complete allowed write payload and compute the request
   fingerprint.
5. Atomically look up the idempotency mapping, then perform canonical admission
   only when no matching mapping exists.

401 unauthenticated and 403 forbidden are terminal at step 1 or 2. They do not
look up or return an idempotency mapping, create a rejection tombstone, reserve
a Job/Session/Input/Turn, write an outbox item, append a public event, or issue
a Runner/provider operation. This makes authentication and authorization prior
to both duplicate reconciliation and admission.

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
acceptance has a jobId but no live sessionId, inputId, or turnId. sequence is
null only when no Session public event could exist. No response contains an
unlisted execution property.

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
| output | Null or { "text": "..." } containing only persisted public final output. It is never a transcript or raw provider response. |
| error | Null or { "code": "stable_public_code", "message": "safe public explanation" }. It never carries a stack trace, provider error, path, or opaque internal identity. |
| acceptedAt, queuedAt, startedAt, terminalAt, observedAt | RFC 3339 UTC timestamps for public canonical facts. observedAt is always present. |
| sequence | The latest persisted public Session event sequence that reflects this observation, or null before a Session exists. |

The allowlist excludes, without exception: runtimeSessionId, Runner IDs, runtime
names, binding epochs, connection IDs, leases, fences, operation IDs, attempt
IDs, dispatch/retry details, prompt or input text, Instructions, memory, tool
state, workspace/workdir/path, attachments, raw payloads, raw transcript facts,
and raw provider or Runner errors. A safe public error may state queue_full or
stop_outcome_unknown; it cannot include the private cause detail.

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
| 400 | cursor_invalid | The opaque cursor is malformed, tampered with, bound to another Project or Session, or cannot be decoded. No fallback or event read is attempted. |
| 401 | unauthenticated | The Bearer PAT is absent, invalid, expired, or revoked. The response includes WWW-Authenticate: Bearer; no idempotency or admission effect occurs. |
| 403 | forbidden | The authenticated caller lacks route scope or access to the selected Project/resource. No idempotency or admission effect occurs. |
| 404 | project_not_found, agent_not_found, session_not_found, input_not_found, or turn_not_found | The requested canonical resource is not available in the caller's authorized Project. 404 is not the public execution state unknown. |
| 409 | idempotency_key_reused | The same durable scope/key was supplied with a different normalized payload. The conflict is stable and creates no new effect. |
| 409 | stop_outcome_unknown | A different stop key attempts to supersede a stop whose fenced outcome is still unknown. The caller must read the Turn; no new stop is issued. |
| 410 | cursor_expired | The cursor was valid but falls before the retained public event floor. The response also includes safe earliestSequence and latestSequence values. The caller reloads current Input/Turn observations before starting at a new retained position. |

Canonical admission rejection is not hidden as an HTTP transport failure. A
well-formed keyed launch or follow-up that receives a durable rejection returns
200 with status=terminal, outcome=rejected, and a safe public error. That is the
only form that makes response-loss replay return the same durable decision
without inventing an Input or Turn later.

## Persisted public Session events

### Scope and shape

GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events reads one
Session's public projection. It never reads a Project-wide mixed stream. after
is an optional opaque cursor and limit is optional, with a default of 100 and a
maximum of 100. The resume rule is exclusively **after**: the page contains
only events whose sequence is greater than the position encoded by after. There
is no implicit inclusive replay mode.

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

type is a finite public vocabulary: input.accepted, input.rejected, turn.queued,
turn.running, turn.outcome_pending, turn.terminal, and session.unknown. The
event's execution is exactly PublicExecutionRead; there is no second event
payload schema and no raw event data. An event cursor is the exclusive
continuation position immediately after that event; nextCursor equals the last
event cursor in a non-empty page.

The Server appends a public event in the same durable state transition that
makes its projected public fact observable. Each Session's sequence is a
strictly increasing positive integer. It never reuses or renumbers a sequence,
and an event page is sorted ascending by sequence. The cursor is opaque,
tamper-evident, and bound to this Project, Session, stream generation, and
exclusive sequence position. Clients treat it as data, not as a parseable ID.

### Resume, duplicates, and ordering

The response's nextCursor is positioned after the last returned event. For an
empty page it is positioned at the page's highWaterSequence. A client stores
that cursor only after it durably processes the page. Retrying a GET can return
the same page; concurrent page requests can arrive out of order. The client
deduplicates by (sessionId, sequence), applies events in ascending sequence, and
does not infer a missing transition from a later sequence. When it observes a
gap, it resumes from its last contiguous cursor or rereads the target Input or
Turn.

The Server does not source this route from an in-memory event bus, SignalR hub,
Runner notification, or UI timeline. Those channels can be delayed, duplicated,
or absent. They may notify a client to reread this persisted route, but cannot
define its cursor, ordering, or payload.

### Retention and compaction

V1 retains every public event while its AgentSession is retained. Ordinary
transcript compaction does not compact this public event stream. There is no
time-based public event compaction in v1.

If a future retained-history operation needs to reclaim a public prefix, it may
do so only by atomically retaining a public snapshot event with a later,
unchanged sequence and persisting an earliestSequence floor. Cursors before that
floor return 410 cursor_expired; the Server never silently restarts them at the
beginning or current head. The client reloads its known Input/Turn observations,
then explicitly begins from the new retained position. Permanent Session deletion
also invalidates its public stream and does not turn an old cursor into another
Session's history.

## Stop, terminal fences, and unknown outcomes

POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop is the only
external control operation. It targets a canonical Turn; it cannot name a
Runner, Runtime Session, dispatch attempt, or internal operation.

After authentication and authorization, the Server uses the stop key scope and
fingerprint above to find or persist the existing canonical stop operation. A
queued Turn can become canonically cancelled. A running Turn invokes the
existing fenced stop lifecycle from [agent-execution.md](agent-execution.md).
The external projection does not reveal its fence token, binding, owner, lease,
or provider identity.

A terminal commit is fenced against the complete canonical target. Once it
persists, the Server appends one terminal public event for that Turn and rejects
late non-terminal writes. If execution completion wins a race with stop, that
terminal execution result is returned. If stop wins, the terminal cancelled
result is returned. A late response from either side cannot replace the terminal
outcome, output, error, or sequence.

If a stop response or its provider result is uncertain before a fenced terminal
fact exists, the Turn is unknown and admission remains blocked. Retrying the
same stop key returns that exact durable stop observation without creating a
second stop. The Server does not automatically replay it. A new stop key is
rejected with stop_outcome_unknown until canonical reconciliation resolves the
original fact; the caller can only read the Turn and resume public events.

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

## Prerequisites and implementation slices

This specification remains tracked by #387; these slices do not create new
issues or a parallel milestone.

| Dependency | State on 2026-08-09 | Required relationship |
|---|---|---|
| [#376](https://github.com/suraciii/mohist/issues/376) reusable mohist/agent Workflow Action | Open | Before external launch is released, Workflow Action and direct API must create/read the same canonical Job/Session/Input/Turn lifecycle rather than divergent launch paths. |
| [#382](https://github.com/suraciii/mohist/issues/382) capacity and queued state | Closed | The API reuses its canonical capacity/admission facts; it must not add a second queue or reinterpret retryable blocking as terminal. |
| [#384](https://github.com/suraciii/mohist/issues/384) default public result projection | Closed | The API may reuse its public result facts, but must apply this document's smaller external allowlist rather than export a transcript or UI projection. |
| [#385](https://github.com/suraciii/mohist/issues/385) Agent history and Session timeline | Open | Public event release waits for durable history ordering and retention decisions to be reconciled with this persisted, cursor-based Session projection. |

1. **Bearer admission gate**: deliver authenticated direct caller reads and writes with PAT scope/project checks before every idempotency or admission path; it depends on the auth.md credential model and has no new lifecycle dependency.
2. **Keyed canonical launch and follow-up**: deliver response-loss-safe external submission and Input/Turn reads using Server-computed normalized fingerprints; it depends on #376 sharing the canonical launch path and on #382's existing admission facts.
3. **Persisted public observation**: deliver the strict allowlisted result/error projection and Session event continuation; it depends on #384's public result facts and #385's settled history ordering/retention boundary.
4. **Fenced public stop and recovery**: deliver stop reads whose terminal/unknown result cannot be overwritten or replayed automatically; it depends on the preceding canonical mapping and the existing fenced Turn lifecycle.

## Acceptance criteria

- A missing, invalid, expired, revoked, out-of-scope, or wrong-Project Bearer
  caller receives the documented 401 or 403 before any idempotency lookup,
  rejection tombstone, canonical record, public event, outbox entry, or Runner
  effect exists.
- A launch with one (projectId, agentId, Idempotency-Key) creates at most one
  canonical Job/Session/Input/Turn mapping. A matching retry returns those same
  IDs; a changed normalized payload always returns idempotency_key_reused and
  has zero new effect.
- A follow-up with one (sessionId, Idempotency-Key) creates at most one
  Input/Turn mapping or one durable rejection. Project and Agent are derived
  from the canonical Session, not trusted from caller input.
- Input and Turn reads expose every aggregate state and the component facts that
  distinguish rejected, retryably blocked, terminally blocked, outcome-pending,
  and unknown without inventing a second lifecycle.
- Every direct response and persisted public event passes the strict allowlist;
  no response or event serializes a Runtime Session ID, runner/connection data,
  prompt/input, memory, workdir/path, raw payload, or Runner control field.
- An event cursor resumes exclusively after its position; sequences are
  monotonic per Session, pages are ordered, duplicate/out-of-order delivery is
  safe to deduplicate, invalid cursors return cursor_invalid, and expired
  cursors return cursor_expired without an implicit reset.
- A stop race emits at most one terminal public fact for a Turn. Terminal state
  cannot be overwritten by a late stop/execution result; unresolved stop and
  execution facts remain unknown and never cause automatic replay.

## Deterministic verification matrix

| Scenario | Controlled seam | Required assertion |
|---|---|---|
| Invalid PAT, expired PAT, readonly write, wrong Project | Fake credential/project authorizer plus recording idempotency/admission store | Exact 401 or 403; zero mapping reads/returns, durable writes, public events, outbox records, and Runner calls. |
| First launch, response-loss retry, concurrent same-key launch | Deterministic transaction barrier and fake Job/Session stores | One Job/Session/Input/Turn mapping only; all matching callers receive the same IDs. |
| Launch key reused with changed text or unknown JSON property | Canonical JSON normalizer and recording stores | Stable 409 idempotency_key_reused or 400 invalid_request; no new record/effect. |
| Follow-up key scope | Two Sessions for one Agent and two Agents in one Project | Same key is isolated by Session; the request never accepts caller-provided derived Project/Agent values. |
| Rejected, retryably blocked, outcome-pending, terminal blocked, completed, and unknown | Fake canonical state rows | The five-state aggregate and all component fields follow the precedence table exactly. |
| Projection leakage | Sentinel values in every internal binding, operation, workspace, prompt, memory, path, runner, and raw-error field | JSON responses/events contain only allowlisted keys and never contain any sentinel. |
| Ordered continuation and GET retry | Persisted in-memory public event journal with injected sequence allocator | Pages are ascending, after is exclusive, nextCursor resumes after the last event, and duplicate pages deduplicate by (sessionId, sequence). |
| Concurrent out-of-order pages and gap | Deterministic client reducer plus reordered page delivery | Client applies only ascending contiguous sequence and rereads/resumes rather than inventing a missing transition. |
| Tampered, cross-session, and compacted cursors | Cursor codec and injected retained floor | 400 cursor_invalid for malformed/scope mismatch; 410 cursor_expired with safe sequence bounds for a valid stale cursor; neither causes an implicit restart. |
| Stop versus completion and stop response loss | Fenced fake Runtime with barriers and injected TimeProvider | One terminal event/output/error survives; late completion/stop writes fail closed; unresolved outcome stays unknown, creates no replay, and a new stop key is rejected. |

## Implementation gap

This is target behavior only. The current route surface does not yet provide the
direct PAT-only API, Server-computed external idempotency mapping, strict public
projection, or persisted cursor stream defined here. Those gaps remain tracked
by [#387](https://github.com/suraciii/mohist/issues/387) and its four slices
above. No source implementation is implied by this specification change.
