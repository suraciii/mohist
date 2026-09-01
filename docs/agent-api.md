# External Agent API

An External Agent or automation process can delegate work to a configured
Mohist Agent without the Web UI or a Runner credential. The direct API makes
response loss recoverable: repeat the same keyed request or resume a persisted
Session event stream instead of creating new work.

The versioned direct API is under `/api/v1`. It reuses the canonical
AgentJob, AgentSession, SessionInput, and AgentTurn lifecycle and exposes no
Runner, Runtime, Workspace, prompt, transcript, or provider details.

## Product Commitments

- Every write is authenticated by a Bearer PAT and protected by an
  `Idempotency-Key`.
- A matching retry returns the original canonical IDs and current public
  observation. It never starts duplicate work.
- Public reads expose one five-value aggregate status beside safe component
  facts, not Mohist's internal execution model.
- Event reads use a durable per-Session stream with opaque cursors. Clients can
  resume after response loss without replaying execution.
- Unauthorized requests have no lookup, admission, persistence, or external
  side effect.

## Routes

Route IDs are canonical Mohist IDs, not display names. `projectId` is the
selected private Project. Every request carries a Bearer personal access token
(PAT), and every write carries an `Idempotency-Key` header.

Writes require the `operator` Scope:

- `POST /api/v1/projects/{projectId}/agents/{agentId}/launch` with body
  `{"text":"..."}` returns `PublicExecutionRead` for the launch.
- `POST /api/v1/projects/{projectId}/agent-sessions/{sessionId}/inputs` with
  body `{"text":"..."}` returns `PublicExecutionRead` for the Input and Turn.
- `POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop` with an empty
  body returns `PublicExecutionRead` for the Turn.

Reads accept `readonly` or `operator`:

- `GET /api/v1/projects/{projectId}/agent-jobs/{jobId}` returns
  `PublicExecutionRead` anchored to the Job.
- `GET /api/v1/projects/{projectId}/agent-inputs/{inputId}` returns
  `PublicExecutionRead` anchored to the Input.
- `GET /api/v1/projects/{projectId}/agent-turns/{turnId}` returns
  `PublicExecutionRead` anchored to the Turn.
- `GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events` accepts
  optional `after` and `limit` query parameters and returns `PublicEventPage`.

A command returns `200` when its durable keyed outcome is known. `200` does not
mean execution has finished. The response body is the current public
observation. A still-converging command may return a retryable `503`; repeat the
same keyed request.

## Project Default Execution Configuration

`PUT /api/projects/{projectRef}/default-execution-config` sets the Project
default execution configuration. The body carries `runtime`, `model`, and an
optional `variant`. The Project read reports `defaultExecutionConfig`, or
`null` when unset. A new default replaces the previous one. An invalid default
is rejected and leaves the previous default untouched. See
[Agents and AgentSessions](agent-sessions.md#project-default-execution-configuration)
for resolution at launch.

## Task-First Launch

`POST /api/projects/{projectRef}/agent-tasks` starts work for a caller that has
a task but does not yet need to configure an Agent. The body accepts exactly
these JSON fields: `prompt`, `attachments`, `context`, `name`, `runtime`,
`model`, `variant`, `allowedSubagentAgentIds`, and `maxConcurrentRuns`.

A non-null collaborator list contains Agent IDs from the same Project. A
non-null concurrency limit is a positive integer. `context` uses the same
`issueNumber`, `epicNumber`, `repository`, `workspace`, `workspacePath`, and
`targetId` references as a definition-first launch. The request requires an
`Idempotency-Key`. The Server derives missing Definition fields, materializes
the resolved execution configuration, creates the Agent, then uses the
canonical AgentJob and AgentSession launch pipeline.

Task-first replay uses the same key space as definition-first launch. A retry
with the same key and caller-visible inputs returns the original Agent, Job,
Session, Input, Turn, Workspace, attachment result, and canonical URLs. A
changed prompt, context, attachment list, name, runtime, model, variant,
collaborator list, or concurrency limit returns `409
launch_idempotency_conflict`. A still-converging launch returns
`503 launch_setup_pending` and keeps the same key. A recorded rejection is
replayed as the same rejection. Retry a pending launch with its original key,
not a new task.

## Authentication

Every direct API request must carry a PAT in the HTTP Authorization header:

~~~text literal
Authorization: Bearer <PAT>
~~~

Create a PAT with a persisted Project grant before calling the direct API. Use
an explicit grant for one or more Projects:

~~~text literal
mo auth token create --name release-agent --scope operator --ttl 720 --project proj_123
mo auth token create --name observer --scope readonly --ttl 720 --project proj_123
~~~

An operator PAT may instead use the explicit `operator_all` grant:

~~~text literal
mo auth token create --name owner-agent --scope operator --ttl 720 --all-projects
~~~

`--project` and `--all-projects` are mutually exclusive. `--all-projects`
requires `operator`. Operator Scope alone does not grant access to every
Project. A PAT without a persisted Project grant remains usable on existing
control-plane routes but cannot call `/api/v1`.

The direct boundary accepts no cookie or Agent Connection identity. A missing,
expired, revoked, non-PAT, or `mohist_session` credential receives `401` with
`WWW-Authenticate: Bearer` and the same non-classifying `unauthenticated`
error. A valid PAT without the required Scope, without a usable Project grant,
or outside its grant receives `403 forbidden`.

Mohist authenticates the PAT, resolves its persisted grant, checks route Scope,
and checks the selected Project before resource or request-mapping lookup. An
out-of-grant Project returns `403` even when it does not exist. A missing or
foreign Agent, Job, Session, Input, or Turn returns its resource-specific `404`
only after the Project grant passes.

Authentication and authorization failures do not validate a body, read or write
idempotency mappings, reserve execution records, append public events, or
contact a Runner.

## Writes And Idempotency

Launch, follow-up, and stop require an `Idempotency-Key`. It is an opaque
printable ASCII string from 1 through 128 characters. It is not a JSON field,
Input ID, trace ID, or caller-supplied fingerprint.

Launch and follow-up bodies accept exactly one property, `text`, which must be a
non-empty JSON string. Invalid JSON, duplicate properties, unknown properties,
a missing `text`, and an empty string return `400 invalid_request` before
admission. Whitespace is significant and preserved. The stop body is empty.

The durable scope depends on the command:

- Launch uses `projectId`, `agentId`, and the key. Replay returns the original
  Job, Session, Input, and Turn mapping.
- Follow-up uses `sessionId` and the key. Replay returns the original Input and
  Turn mapping; Project and Agent come from the Session.
- Stop uses `turnId`, caller identity, and the key. Replay returns the original
  stop mapping and Turn observation.

The mapping is written before the canonical command completes. A retry with the
same scope, key, and body returns the original IDs and current observation. It
never creates another execution, Input, Turn, queue entry, or public event.

Reusing a key with a different accepted body returns stable `409
idempotency_key_reused` and creates no effect. A well-formed launch or
follow-up rejected at admission is stored under its key and returns `200` with
`status=terminal` and `outcome=rejected`. Repeating it returns the same
rejection after capacity recovers.

A follow-up derives Project and Agent from the canonical Session. The caller
cannot put either in the body or use a body value to change the fingerprint. A
launch response remains recoverable through the same key even when the response
was lost before the caller learned the Job ID.

Stop targets one canonical Turn. Stopping an already-terminal Turn is a durable
no-op. A queued Turn is cancelled locally without a Runtime call. A running
Turn is recorded cancelled only after Runtime confirms the stop. The first
terminal completion or stop result wins. A matching retry uses the same frozen
target and does not issue another stop effect. While a stop outcome is
unresolved, another key returns `409 stop_outcome_unknown` and cannot supersede
it.

See [External Agent API design](../design/agent-api.md#normalized-fingerprint-and-idempotency)
for canonical fingerprint inputs and cross-aggregate projection behavior.

## Public Execution Reads

Every command and resource read returns the same strict `PublicExecutionRead`
shape. Every key is present; a missing fact is `null`.

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

- `accepted`: the request is durably accepted, but current work is not queued.
- `queued`: work is accepted and waiting for capacity or another retryable
  condition.
- `running`: the current Turn runs or its outcome is being confirmed.
- `terminal`: work completed, failed, was cancelled, was permanently blocked,
  or was rejected before execution.
- `unknown`: consumed durable facts do not confirm acceptance, dispatch,
  binding, stop, or outcome.

The component fields preserve detail. `jobStatus` is `preparing`, `queued`,
`running`, `terminal`, `unknown`, or `null`. `sessionActivity` is `idle`,
`active`, `unknown`, or `null`. `admission` is `ready`, `blocked`, or `null`.
`inputStatus` is `accepted`, `rejected`, `unknown`, or `null`. `turnStatus` is
`queued`, `running`, `outcome_pending`, `terminal`, `unknown`, or `null`.
`outcome` is `completed`, `rejected`, `failed`, `cancelled`, `blocked`, or
`null`.

A terminal Input or Turn stays terminal even when its Session has an active
later Turn. See [External Agent API design](../design/agent-api.md#five-state-mapping-and-precedence)
for precedence rules.

A prepared launch is Job-anchored. Its first public observation may have
`status=accepted`, `jobStatus=preparing`, and null Session, Input, and Turn IDs.
After Session acceptance, the same Job read exposes the joined IDs. A durable
rejection may remain Job-anchored with `status=terminal`, `outcome=rejected`, a
safe error and reason code, and null live IDs. A follow-up has `jobId: null`.

`output` is null or `{ "text": "..." }` containing persisted public final
output. `error` contains only a stable public `code` and safe `message`. The
response never contains prompt text, transcripts, Runner or Runtime IDs,
Workspace paths, leases, fences, operation IDs, retry details, raw provider
errors, or other internal execution fields.

## Projection Freshness And Errors

Public Job, Input, Turn, command-replay, and event responses come from the
persisted public projection. If it has not caught up with required source facts,
the response is `503 projection_lag` with a `Retry-After` hint and no stale
execution body. Retry the same read or keyed command. Projection lag is not
public `unknown` and creates no effect.

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

- `400 invalid_request`: body or query is invalid.
- `400 idempotency_key_required` or `idempotency_key_invalid`: the write key is
  missing or outside the accepted form.
- `400 cursor_invalid`: the cursor is malformed, tampered with, or bound to
  another stream.
- `401 unauthenticated`: no usable Bearer PAT; the response includes the
  Bearer challenge.
- `403 forbidden`: Scope or persisted Project authorization failed before
  lookup.
- `404 agent_not_found`, `job_not_found`, `session_not_found`,
  `input_not_found`, or `turn_not_found`: the resource is absent from the
  authorized Project.
- `409 idempotency_key_reused`: the key was used with a different accepted
  request.
- `409 stop_outcome_unknown`: another stop for the Turn is unresolved.
- `410 cursor_expired`: the valid cursor is before the retained event floor;
  safe sequence bounds are included.
- `503 projection_lag`: the public projection has not caught up with required
  source facts.
- `503 stop_pending`: the stop outcome is not confirmed; retry the same keyed
  stop request.

## Resume Session Events

The event route reads one Session's persisted public stream:

`GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events`

`limit` defaults to 100 and is capped at 100. `after` is an opaque cursor. The
page contains only events with a sequence strictly greater than that cursor. A
request without `after` starts at the beginning of the retained stream. The
response contains `sessionId`, an ascending `events` array, `nextCursor`, and
`highWaterSequence`.

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

A malformed, tampered, cross-Project, cross-Session, or wrong-generation
cursor returns `400 cursor_invalid`. Server does not fall back to the beginning
or head and does not attempt an event read. A valid cursor before the retained
floor returns `410 cursor_expired` with `earliestSequence` and
`latestSequence`; it does not silently restart. During a closed-stream
tombstone window, a valid cursor returns `410 cursor_expired` with null
`earliestSequence` and the last safe `latestSequence`. A request without a valid
cursor returns `404 session_not_found`. After tombstone purge, the old cursor is
`400 cursor_invalid`.

Sequences increase across stream generations and are never reused. A client
stores a cursor only after processing the page, deduplicates by
`(sessionId, sequence)`, and resumes from its last contiguous cursor when it
finds a gap. The stream remains separate from in-memory notifications,
SignalR, Runner events, and product transcripts.

## Privacy Boundary

The direct API exposes canonical IDs, public state, safe final output, safe
errors, timestamps, event sequences, and opaque cursors. It does not expose
prompt or Input content, Instructions, memory, tool state, Workspace paths,
Runtime Sessions, Runner or Connection identity, leases, fences, operation IDs,
raw payloads, or provider diagnostics.
