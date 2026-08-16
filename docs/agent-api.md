# External Agent API

An external Agent or automation process can delegate work to a configured
Mohist Agent without opening the Web UI or using a Runner credential. The direct
API also makes response loss recoverable: the caller repeats the same keyed
request or resumes a persisted Session event stream instead of creating new
work.

The API is available under `/api/v1`. It uses the existing AgentJob,
AgentSession, SessionInput, and AgentTurn lifecycle. It does not expose Runner,
Runtime, workspace, prompt, transcript, or provider details.

## Routes

Route IDs are canonical Mohist IDs. The `projectId` in every route is the
selected private Project.

| Method and route | Scope | Request | Response |
|---|---|---|---|
| `POST /api/v1/projects/{projectId}/agents/{agentId}/launch` | `operator` | Bearer PAT, `Idempotency-Key`, `{"text":"..."}` | `PublicExecutionRead` for the launch |
| `POST /api/v1/projects/{projectId}/agent-sessions/{sessionId}/inputs` | `operator` | Bearer PAT, `Idempotency-Key`, `{"text":"..."}` | `PublicExecutionRead` for the Input and Turn |
| `GET /api/v1/projects/{projectId}/agent-jobs/{jobId}` | `readonly` or `operator` | Bearer PAT | `PublicExecutionRead` anchored to the Job |
| `GET /api/v1/projects/{projectId}/agent-inputs/{inputId}` | `readonly` or `operator` | Bearer PAT | `PublicExecutionRead` anchored to the Input |
| `GET /api/v1/projects/{projectId}/agent-turns/{turnId}` | `readonly` or `operator` | Bearer PAT | `PublicExecutionRead` anchored to the Turn |
| `GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events` | `readonly` or `operator` | Bearer PAT, optional `after` and `limit` | `PublicEventPage` |
| `POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop` | `operator` | Bearer PAT, `Idempotency-Key`, empty body | `PublicExecutionRead` for the Turn |

A command returns `200` when its durable keyed outcome is known. `200` does
not mean that execution has finished. The response body is the current public
observation. A command that is still converging can return a retryable `503`;
repeat the same keyed request.

## Authentication

Every direct API request must carry a PAT in the HTTP Authorization header:

~~~text literal
Authorization: Bearer <PAT>
~~~

Create a PAT with a persisted Project grant before calling the direct API. Use
an explicit grant for one or more Projects:

~~~text literal
mo auth token create --name release-agent --scope operator --ttl 720h --project proj_123
mo auth token create --name observer --scope readonly --ttl 720h --project proj_123
~~~

An operator PAT may instead use the explicit `operator_all` grant:

~~~text literal
mo auth token create --name owner-agent --scope operator --ttl 720h --all-projects
~~~

`--project` and `--all-projects` are mutually exclusive. `--all-projects`
requires `operator`. Operator Scope alone does not grant access to every
Project. A PAT without a persisted Project grant remains usable on existing
control-plane routes but cannot call `/api/v1`.

The direct boundary accepts no cookie or Agent Connection identity. A request
that uses `mohist_session`, a missing PAT, an expired PAT, a revoked PAT, or a
non-PAT credential receives `401` with `WWW-Authenticate: Bearer`. The body is
the same non-classifying `unauthenticated` error for all of these cases.

A valid PAT without the required Scope, without a usable Project grant, or
outside its explicit Project grant receives `403 forbidden`. Mohist checks the
Bearer PAT, resolves its persisted Project grant, checks route Scope, and then
checks the selected Project before it looks up a resource or request mapping.
An out-of-grant Project returns `403` even when the Project does not exist. A
missing or foreign Agent, Job, Session, Input, or Turn returns its resource
specific `404` only after the Project grant passes.

Authentication and authorization failures have no effects. They do not read or
write idempotency mappings, validate a body, reserve execution records, append
public events, or contact a Runner.

## Writes And Idempotency

Launch, follow-up, and stop each require an `Idempotency-Key` header. The key is
an opaque printable ASCII string from 1 through 128 characters. It is not a
JSON field, an Input ID, or a caller-supplied fingerprint.

Launch and follow-up bodies accept exactly one property, `text`. It must be a
non-empty JSON string. Invalid JSON, duplicate properties, unknown properties,
a missing `text`, and an empty string return `400 invalid_request` before
admission. Whitespace is significant and is preserved. The stop body must be
empty.

The Server parses the accepted body once and computes a versioned SHA-256
fingerprint from the command, canonical route IDs, and accepted body. It does
not trim or case-fold the text. The durable scopes are:

| Command | Durable scope | Replay identity |
|---|---|---|
| Launch | `projectId`, `agentId`, `Idempotency-Key` | The original Job, Session, Input, and Turn mapping |
| Follow-up | `sessionId`, `Idempotency-Key` | The original Input and Turn mapping; Project and Agent come from the Session |
| Stop | `turnId`, caller identity, `Idempotency-Key` | The original fenced stop mapping and Turn observation |

The mapping is written before the canonical command completes. A retry with the
same scope, key, and body returns the original IDs and current observation. It
never creates another execution, Input, Turn, queue entry, or public event.

Reusing a key with a different accepted body returns stable `409
idempotency_key_reused`. The conflicting request creates no new effect. A
well-formed launch or follow-up that is definitively rejected at admission is
stored under its key and returns `200` with `status=terminal` and
`outcome=rejected`. Repeating the request returns the same rejection even after
capacity recovers.

A follow-up derives Project and Agent from the canonical Session. The caller
cannot put either value in the body or use a body value to change the
fingerprint. A launch response can be recovered through the same key even when
the response was lost before the caller learned the Job ID.

Stop targets one canonical Turn. Stopping an already-terminal Turn is a durable
no-op. A queued Turn is cancelled locally without a Runtime call. A running
Turn uses the existing fenced stop lifecycle. The first terminal completion or
stop result wins. A matching retry uses the same frozen target and does not
issue another stop effect. While a stop outcome is unresolved, another key for
the Turn returns `409 stop_outcome_unknown` and cannot supersede it.

## Public Execution Reads

Every command and resource read returns the same strict `PublicExecutionRead`
shape. Every key is present; a fact that does not exist is represented by
`null`.

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

The aggregate `status` has exactly five values:

| Status | Meaning |
|---|---|
| `accepted` | Mohist accepted the request, but the current work is not yet queued. |
| `queued` | Work is accepted and waiting for capacity or another retryable condition. |
| `running` | The current Turn is running or its outcome is still being confirmed. |
| `terminal` | Work completed, failed, was cancelled, was blocked permanently, or was rejected before execution. |
| `unknown` | Consumed durable facts do not confirm acceptance, dispatch, binding, stop, or outcome. |

The component fields preserve detail beside the aggregate. `jobStatus` is
`preparing`, `queued`, `running`, `terminal`, `unknown`, or `null`.
`sessionActivity` is `idle`, `active`, `unknown`, or `null`. `admission` is
`ready`, `blocked`, or `null`. `inputStatus` is `accepted`, `rejected`,
`unknown`, or `null`. `turnStatus` is `queued`, `running`,
`outcome_pending`, `terminal`, `unknown`, or `null`.

The aggregate precedence is fixed: a fenced terminal fact wins, then a durable
rejection, then an unresolved fact produces `unknown`, then `outcome_pending`
produces `running`, then a retryable blocked dispatch remains `queued`, and
finally `running` wins over `queued`, which wins over `accepted`. A terminal
Input or Turn stays terminal even when its Session is active because a later
Turn is running.

A prepared launch is anchored by its Job. Its first public observation can have
`status=accepted`, `jobStatus=preparing`, and null Session, Input, and Turn IDs.
After Session acceptance, the same Job read exposes the joined public IDs. A
durable rejection can remain Job-anchored with `status=terminal`,
`outcome=rejected`, a safe error and reason code, and null live IDs. A
follow-up has `jobId: null` because it targets an existing Session.

`output` is either `null` or `{ "text": "..." }` containing only persisted
public final output. `error` contains only a stable public `code` and safe
`message`. The response never contains prompt text, transcripts, Runner or
Runtime IDs, workspace paths, leases, fences, operation IDs, retry details, raw
provider errors, or other internal execution fields.

## Projection Freshness And Errors

Public Job, Input, Turn, command-replay, and event responses come from the
persisted public projection. The Server compares the required canonical source
watermark with the projection checkpoint before serving current state. If the
checkpoint is behind, the response is `503 projection_lag` with a `Retry-After`
hint and no stale execution body. Retry the same read or the same keyed command.
Projection lag is not the public `unknown` state and does not create an effect.

Errors use this envelope:

~~~json
{
  "error": {
    "code": "cursor_invalid",
    "message": "The event cursor is invalid or is not bound to this request."
  }
}
~~~

Common responses are:

| HTTP | Code | Meaning |
|---|---|---|
| `400` | `invalid_request` | The body or query is invalid. |
| `400` | `idempotency_key_required` or `idempotency_key_invalid` | The write key is missing or outside the accepted form. |
| `400` | `cursor_invalid` | The cursor is malformed, tampered with, or bound to another stream. |
| `401` | `unauthenticated` | The request has no usable Bearer PAT. The response includes the Bearer challenge. |
| `403` | `forbidden` | Scope or persisted Project authorization failed before lookup. |
| `404` | `agent_not_found`, `job_not_found`, `session_not_found`, `input_not_found`, or `turn_not_found` | The resource is absent from the authorized Project. |
| `409` | `idempotency_key_reused` | The same key was used with a different accepted request. |
| `409` | `stop_outcome_unknown` | Another stop for the Turn is unresolved. |
| `410` | `cursor_expired` | The valid cursor is before the retained event floor; safe sequence bounds are included. |
| `503` | `projection_lag` | The public projection has not caught up with the required source facts. |

## Resume Session Events

The event route reads one Session's persisted public stream:

`GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events`

The optional `limit` defaults to 100 and is capped at 100. The optional `after`
value is an opaque cursor. The page contains only events with a sequence
strictly greater than the cursor position. A request without `after` starts at
the beginning of the retained stream. The response contains `sessionId`, an
ascending `events` array, `nextCursor`, and `highWaterSequence`.

A cursor is tamper-evident and bound to the Project, Session, stream generation,
and exclusive sequence position. Treat it as opaque data. Do not parse or
construct it. `nextCursor` is the last event cursor on a non-empty page. On an
empty page it is positioned at `highWaterSequence`.

Execution events are limited to `input.accepted`, `input.rejected`,
`turn.queued`, `turn.running`, `turn.outcome_pending`, `turn.terminal`, and
`session.unknown`. Each carries the full `PublicExecutionRead` object. A
`session.context_reset` event carries only this six-key `session` object:

~~~json
{
  "projectId": "proj_123",
  "agentId": "agent_123",
  "sessionId": "session_123",
  "sessionActivity": "idle",
  "admission": "ready",
  "reasonCode": "context_reset"
}
~~~

A malformed, tampered, cross-Project, cross-Session, or wrong-generation cursor
returns `400 cursor_invalid`. The Server does not fall back to the beginning or
head and does not attempt an event read. A valid cursor before the retained
floor returns `410 cursor_expired` with `earliestSequence` and
`latestSequence`; it does not silently restart. During a closed-stream
tombstone window, a valid cursor returns `410 cursor_expired` with a null
`earliestSequence` and the last safe `latestSequence`. A request without a
valid cursor returns `404 session_not_found`. After the tombstone is purged,
the old cursor is `400 cursor_invalid`.

Sequences increase across stream generations and are never reused. A client
stores a cursor only after it processes the page, deduplicates by
`(sessionId, sequence)`, and resumes from its last contiguous cursor when it
finds a gap. The event stream remains separate from in-memory notifications,
SignalR, Runner events, and product transcripts.

## Privacy Boundary

The direct API exposes canonical IDs, public state, safe final output, safe
errors, timestamps, event sequences, and opaque cursors. It does not expose
prompt or Input content, internal instructions, memory, tool state, workspace
paths, Runtime Sessions, Runner or Connection identity, leases, fences,
operation IDs, raw payloads, or provider diagnostics.
