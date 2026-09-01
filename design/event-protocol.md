# Event Protocol

This document defines Mohist's event envelope and project-scoped live delivery
protocol. Web and non-SignalR clients, including `mo`, use the same native
WebSocket. Event-bus subscriptions, Agent routing, and live notifications use
the same event vocabulary. See [`eventbus.md`](eventbus.md) for persistence and
delivery and [`event-routing.md`](event-routing.md) for the Agent-facing
routing table.

## Core Decisions

- An envelope separates event type, emitting source, and business lineage.
- Producers stamp lineage once. Consumers route from the envelope and never
  query domain state to decide whether an event matched.
- Event payloads are private to their domain. Routing uses promoted context
  attributes only.
- System subscriptions and Agent routing use the same event vocabulary.
- The live socket is project-scoped and best effort. Its subscription state is
  connection-local and has no replay cursor or durable queue.

## Three Orthogonal Axes

Every envelope separates three facts: `type` says what happened, `source`
says which entity emitted it, and context extension attributes identify its
business lineage. Producers stamp lineage when they create the event, so a
subscriber can match `Issue #42` without querying domain state.

## `type`: Event Taxonomy

Types use `com.mohist.<domain>.<event>` and are registered in `EventCatalog`.
The Catalog lists stable types. The event family and structure determine
lineage requirements; the Catalog does not duplicate those schemas.

### Historical Workflow interruption events

`com.mohist.workflow.task.interrupted` and
`com.mohist.workflow.checks.interrupted` remain stable historical event types.
Retiring interrupted WorkflowRun current state does not remove their catalog
entries, serializers, deserializers, queries, lineage, or historical
presentation.

These events record what an older Workflow implementation observed at production
time. A consumer may present that occurrence as history, but cannot use it to
reconstruct current WorkflowRun, WorkflowActionAttempt, or checks state; decide
retry or recovery; or infer that current work is running. Current state comes
only from the owning aggregate. After current interrupted state is retired, no
new production path emits these event types.

## `source`: Emitting Entity

`source` uses the emitting entity's domain identity, such as
`/mohist/workflow-runs/{workflowRunId}`,
`/mohist/projects/{projectId}/issues/{issueNumber}`, or
`/mohist/projects/{projectId}/epics/{epicNumber}`. Project scope is part of an
Issue or Epic identity. Mutable lineage such as Epic membership or Workflow
origin is not encoded in `source`.

## Context Attributes: Business-Lineage Stamping

### Rules

1. The producer stamps flat extension attributes from lineage held by its
   aggregate. It does not query another aggregate.
2. Matchers and dispatchers read only the envelope. A reaction handler may read
   current state before issuing an idempotent command, but that read cannot
   change whether the route matched.
3. Attributes record ownership at production time. Moving an Issue to another
   Epic does not rewrite historical events.
4. Promote a business identity to an envelope attribute only when it is a
   routing dimension. Payload `data` never participates in routing.

### Names

CloudEvents extension names contain only lowercase letters and digits. A
business entity uses the shortest accurate name for its unique identity:

- `projectid`: Global Project identity.
- `issue`, `epic`: Issue or Epic number within a Project and therefore part of
  its domain identity.
- `workflowrunid`, `agentid`, `sessionid`, `runnerid`: The corresponding global
  identities.
- `workspace`: Workspace name within a Project; `projectid` and `workspace`
  together are unique. `workspaceoriginkind` is the creation source:
  `manual`, `issue`, `slack`, or `web`.

An envelope does not carry both `issue` and `issueid`, or both `epic` and
`epicid`. Issues and Epics have no second internal ID, so `issueno` and
`epicno` aliases also do not exist.

### Stamping Rules by Event Family

Each event family stamps a fixed base context:

- `workflow.*`: `projectid` and `workflowrunid` required; `epic` and `issue`
  if present.
- `issue.*`: `projectid` and `issue` required; `epic` if present.
- `epic.*`: `projectid` and `epic` required.
- `agent-session.*`: `projectid` and `sessionid` required; `epic` if present
  for Workflow origin; `issue` and `workflowrunid` for Workflow origin;
  `agentid` for Agent origin.
- `runner.*`: `runnerid` required; `projectid` if present.
- `workspace.*`: `projectid` required; `issue` if present.
- `inbox.item-persisted`: `projectid` and `issue` required; `epic` and
  `workflowrunid` copied from the source event if present.

"If present" means that production must stamp an existing association and
must omit the attribute rather than stamp an empty value when none exists.

`workspace.*` events also stamp `workspace` and `workspaceoriginkind`. Origin
is a Workspace resolution key, so a subscriber responding in an entry-point
context such as a channel or conversation must be able to filter by it.

Any Workflow event that structurally carries a Stage also stamps `stage`. This
includes `workflow.stage.*`, `workflow.task.*`, `workflow.check.*`, and
`workflow.feedback.requested`. The `{{event.stage}}` rendering placeholder
depends on this attribute and no longer parses `data`.

`subject` keeps its CloudEvents meaning and is not a routing key.

## Match Expressions: CEL Subset

A subscription or route matches an envelope with one Boolean expression. The
syntax is a subset compatible with [CEL](https://cel.dev/).

### Syntax

```text literal
expr       := or
or         := and ( "||" and )*
and        := unary ( "&&" unary )*
unary      := "!" unary | primary
primary    := "(" expr ")" | comparison | call | presence
comparison := operand ( "==" | "!=" ) operand
            | attr "in" "[" string ( "," string )* "]"
call       := attr "." func "(" string ")"      func in { startsWith, endsWith, contains, matches }
presence   := "has" "(" attr ")"
operand    := attr | string
attr       := "event" "." ident
string     := double-quoted string literal
```

Examples:

```text literal
event.type.startsWith("com.mohist.workflow.") && event.issue == "42"
event.type == "com.mohist.workflow.run.failed" && event.stage != "plan"
event.issue in ["42", "43"]
event.type == "com.mohist.issue.completed" && has(event.epic)
```

### Semantics

- Every value is a string. `event.<attr>` resolves an envelope property.
  `type`, `source`, `subject`, and every context extension have equal status.
- A **missing attribute evaluates to the empty string `""`**. Use `has()` to
  distinguish missing from empty.
- `matches` performs regular-expression matching with an evaluation timeout.
- There are no loops or function definitions, termination is guaranteed, and
  evaluation is deterministic for the same event and expression.
- **Compile on write**: Reject a create or update when parsing fails. Treat a
  runtime evaluation error as no match and record it in structured logs and a
  counter.
- `event.data.*` is unavailable. Payload structure is private to each domain,
  and routing cannot couple to it. Promote a required business dimension to a
  context attribute under the admission criterion.

## Live WebSocket Protocol

```text diagram
                         +-------+
                         | event |
                         +---+---+
                             |
                             v
                +-------------------------+
                | envelope: type, source, |
                |         context         |
                +------------+------------+
                   +---------+---------+
                   v                   v
               +-------+  +------------------------+
               | match |  | WebSocket notification |
               +---+---+  +------------------------+
                   |
                   v
            +------------+
            | dispatcher |
            +------+-----+
         +---------+--------+
         v                  v
+----------------+   +------------+
| system handler |   | user route |
+----------------+   +------------+
```

The same envelope feeds system handlers, Agent routing, and live notification
projection. The socket never changes event facts or domain state.

### Boundary and Authentication

The live endpoint is:

```text literal
GET /api/projects/{projectRef}/events/socket
```

The request must be a WebSocket upgrade. Before returning `101`, Server resolves
`projectRef`, authenticates the normal Mohist credential, enforces
`IntegrationProjectConstraint`, and requires `readonly` or `operator`. A browser
uses the `mohist_session` cookie. For cookie-authenticated upgrades, Server also
requires `Origin` scheme and authority to exactly equal the request's canonical
scheme and authority; a missing or different value is rejected before upgrade.
The canonical values are normally `Request.Scheme` and `Request.Host`. If and
only if the immediate `RemoteIpAddress` is loopback, Server accepts a pair of
single-valued `X-Forwarded-Proto` and `X-Forwarded-Host` headers from the local
reverse proxy and uses their validated scheme and authority instead. Both
headers must be present together, contain exactly one value with no comma-list,
and parse respectively as a valid HTTP scheme and Host authority; malformed,
multiple, or incomplete forwarded values reject the upgrade. Forwarded headers
from a non-loopback peer are ignored and never affect the canonical origin.
There is no configurable trusted-origin list. `mo` and other non-browser clients
use `Authorization: Bearer <token>` on the upgrade request and are not required
to send `Origin`. Tokens are never accepted in the query string. No Project
identifier is accepted in query parameters or JSON-RPC messages; the resolved
route Project is the connection's immutable scope.

Every delivery path applies strict Project isolation. A domain event is eligible
only when its `projectid` extension exactly equals the resolved Project.
`ITranscriptEventPublisher` exposes
`PublishAsync(projectId, transcriptEnvelope)`. `AgentSessionGrain` reads
`projectId` from its session metadata when it handles the runtime event and
passes that value to the publisher before transcript persistence; it never
queries another aggregate to find the Project. A session with missing Project
metadata does not publish a live transcript event and records the invariant
failure. A task-log publisher must likewise carry a non-null owning Project ID.
Publication metadata must exactly equal the connection Project; missing metadata
never means cross-Project or administrator delivery. The routing Project ID is
not added to the transcript wire shape because the connection already
establishes that scope.

The endpoint is an observation connection, not an event bus or durable
subscription. Subscription state is process-local, belongs to one physical
connection, starts empty, and is discarded on close. Server does not use an
Orleans grain to persist connection state.

### JSON-RPC Profile

The socket uses JSON-RPC 2.0. One UTF-8 WebSocket text message contains exactly
one JSON-RPC object. Batch arrays, binary messages, positional `params`, and a
custom WebSocket subprotocol are outside this profile. Every
`subscription.set` request requires a non-empty string `id`, unique among
requests in flight on that connection. This restricted profile does not support
client JSON-RPC notifications. Server ignores any idless object without
executing it or sending a response and increments the protocol error counter.

Client sends requests and Server sends responses and notifications. Neither
side sends methods in the opposite direction. Standard JSON-RPC errors are used:
Parse Error (`-32700`) for invalid JSON, Invalid Request (`-32600`) for a
malformed object, an invalid request ID, or a duplicate in-flight ID, Method Not
Found (`-32601`) for an unknown method, Invalid Params (`-32602`) for an invalid
`subscription.set` value, and Internal Error (`-32603`) for an unexpected
failure. Invalid params include an invalid match expression and return its
existing `offset`, `line`, `column`, and `source` diagnostic in `error.data`.
Rejected requests do not alter subscription state. Errors echo a valid request
ID; Parse Error and errors for which no valid ID can be established use
`"id": null`. An idless call, including an unknown method, receives no response
and does not execute. Each rejected message increments a per-connection protocol
error counter. On the third error, Server first attempts to enqueue any error
response owed for that message and then closes with `1008`. If that enqueue
fails, the queue-saturation rule takes precedence: Server closes with `1013` and
does not promise delivery of the response.

The only client method is `subscription.set`:

```json
{
  "jsonrpc": "2.0",
  "id": "req_1",
  "method": "subscription.set",
  "params": {
    "domain": {
      "types": ["com.mohist.issue.completed"],
      "match": "event.issue == \"42\""
    },
    "transcript": {
      "types": ["message.delta", "tool_call.completed"]
    },
    "taskLogs": [
      { "workflowRunId": "run_123", "taskId": "task_456" }
    ]
  }
}
```

All three properties are required. `domain` and `transcript` are either their
shown object or `null`; `taskLogs` is an array. The complete request atomically
replaces the connection's prior state:

- `domain: null` disables domain notifications. Otherwise `types` is either a
  non-empty set of exact CloudEvent types or `null` for all domain types.
  `match` is either a non-empty match expression or `null`. When both are
  present, both must match. `{ "types": null, "match": null }` subscribes to
  every project domain event.
- `transcript: null` disables transcript notifications. Otherwise `types` is a
  non-empty set of exact `TranscriptEnvelope.type` values. There is no wildcard
  and no CEL matching for transcript payloads.
- `taskLogs` is the exact set of `(workflowRunId, taskId)` pairs to observe.
  An empty array disables task-log notifications. Task-log interest does not
  also require a synthetic `task-log.delta` type subscription.

Duplicate type values and task scopes are normalized as sets. Domain type values
are trimmed and may be any non-empty exact string within the subscription limits;
an unknown domain type is forward-compatible and simply matches no currently
emitted event. Empty or whitespace domain types and unknown transcript types are
Invalid Params. A task scope need not be looked up while setting a subscription;
strict Project metadata on publication is the authorization boundary and an
unknown scope simply receives nothing.

Successful replacement returns only after the new state is active:

```json
{ "jsonrpc": "2.0", "id": "req_1", "result": {} }
```

Server processes `subscription.set` requests serially in WebSocket receive
order. One per-connection synchronization boundary covers replacing the state
and enqueueing its successful response. A publisher snapshots only connections
indexed by the event's Project and notification kind, then enters the same
per-connection boundary to recheck the current subscription and enqueue an
eligible notification. Thus the successful response is enqueued before every
notification admitted by the new state. The last request sent is the last state
installed, and a client does not need to buffer new-subscription notifications
before its response.

There are no incremental subscribe/unsubscribe methods. A client updates its
interest by sending another complete `subscription.set`. This gives reconnect,
React unmount, and concurrent panel changes one idempotent operation with no
server-side merge semantics. The client owns aggregation of its current task-log
scopes before sending the replacement.

### Server Notifications

Server sends three notification methods. They remain separate because domain
facts, transcript runtime data, and task-log output have different identities,
filters, and reconciliation reads.

`event.domain.params.event` is the same CloudEvents 1.0 structured JSON object
used as the outbound webhook body. JSON-RPC wraps that object but does not
change Mohist's Published Language:

```json
{
  "jsonrpc": "2.0",
  "method": "event.domain",
  "params": {
    "event": {
      "specversion": "1.0",
      "id": "evt_123",
      "source": "/mohist/projects/project_1/issues/42",
      "type": "com.mohist.issue.completed",
      "time": "2026-08-20T12:00:00.0000000+00:00",
      "datacontenttype": "application/json",
      "data": {},
      "projectid": "project_1",
      "issue": "42"
    }
  }
}
```

Standard core attributes use their lowercase CloudEvents names, extension
attributes are top-level properties, and `data` is the event payload. Optional
core attributes follow the outbound webhook rendering contract and are omitted
when absent. The Server reuses `WebhookPayloadRenderer` as the authoritative
renderer and contract for both outbound webhooks and socket domain events; it
does not maintain or serialize a second camelCase `CloudEventEnvelope` DTO for
the socket.

Web intentionally adapts from the legacy SignalR camelCase envelope to this
standard object. `mo event tail` writes each `params.event` object unchanged as
one NDJSON object per output line, and JSON field selection operates on that
object rather than on the JSON-RPC carrier. The old tail shape with lineage
nested under `extensions` is removed; CLI output uses `specversion`,
`datacontenttype`, `data`, and top-level extension attributes.

`event.transcript` carries the current low-latency raw runtime notification
without turning it into a CloudEvent. Server emits it before persistence:

```json
{
  "jsonrpc": "2.0",
  "method": "event.transcript",
  "params": {
    "event": {
      "id": "part_123",
      "sessionId": "session_123",
      "runtimeSessionId": "runtime_123",
      "runtime": "opencode",
      "sequence": 17,
      "type": "message.delta",
      "payload": {},
      "createdAt": "2026-08-20T12:00:00.0000000+00:00"
    }
  }
}
```

`runtimeSessionId` and `runtime` may be `null`; `payload` is any JSON value.
`id` and `sequence` are transient runtime identifiers for this notification
stream. Persistence may coalesce, rename, omit, or resequence runtime events, so
the raw notification and rendered persisted transcript do not share a durable
identity. A disconnect gap in raw runtime notifications is not exactly
replayable from persisted transcript history.

`event.task-log` carries the existing freshly persisted task-log delta:

```json
{
  "jsonrpc": "2.0",
  "method": "event.task-log",
  "params": {
    "delta": {
      "ownerKind": "workflow",
      "ownerId": "run_123",
      "projectId": "project_1",
      "workId": "work_123",
      "taskId": "task_456",
      "entries": [
        {
          "seq": 18,
          "timestamp": "2026-08-20T12:00:01.0000000+00:00",
          "source": "stdout",
          "text": "completed"
        }
      ],
      "truncated": false
    }
  }
}
```

For this project-scoped transport, `projectId` and `taskId` are non-null and
must match the connection and requested task scope. `entries` contains only the
newly persisted lines and may be empty when `truncated` changes. Persistence
always completes before this best-effort notification.

### Limits and Backpressure

Protocol limits are constants, not deployment configuration:

- One complete fragmented text message is at most 4 MiB of UTF-8; an oversized
  inbound message closes with `1009`. If a Server notification serializes above
  the same limit, Server fences each targeted connection with `1009`; the
  authoritative HTTP read remains available for reconciliation.
- A subscription contains at most 256 domain types, 256 transcript types, and
  128 task-log scopes. A match expression is at most 8 KiB of UTF-8. Exceeding a
  subscription limit is Invalid Params and leaves the old subscription active.
- Each connection has one 256-message bounded outgoing queue shared by responses
  and notifications. Socket writes and close output are serialized.
- During authoritative HTTP reconciliation, Web buffers at most 256 transcript
  notifications per session identity and 256 task-log deltas per task scope.
  Overflow clears those transient buffers and closes the socket with private
  code `4000`; the normal reconnect and reconciliation path restores an
  authoritative base instead of allowing unbounded browser memory growth.
  A failed authoritative reconciliation likewise clears its buffers and closes
  with private code `4001`, so delivery cannot remain attached to an unknown
  base or a permanently buffering generation. Any Project invalidation,
  transcript read, or task-log read failure fences delivery for that socket
  generation immediately, aborts every in-progress stream reconciliation, and
  includes the interval before its close callback. Consumers bind the abort
  signal to authoritative query cancellation and check it before local commits.
- Publication never waits for a client. If a response or notification cannot be
  enqueued immediately, Server fences that connection and closes it with `1013`
  instead of silently retaining a connected client with an unknown gap.

The queue owns transport backpressure only. A slow or disconnected client never
blocks a domain publisher, transcript persistence, task-log persistence, or
delivery to another connection. WebSocket Ping/Pong detects dead peers; it is
not an application message and carries no event cursor.

### Ordering, Reconnect, and Reconciliation

Delivery is best-effort from the instant a successful `subscription.set` becomes
active. There is no replay cursor, acknowledgement, durable socket queue, or
global ordering. One connection observes notifications in enqueue order, but
concurrent publishers define no total order. Disconnect races may lose or repeat
notifications. Consumers may deduplicate domain notifications by domain `id`
and task-log entries by `(ownerId, workId, seq)` within those shapes. Raw
transcript `id` and `sequence` do not deduplicate against persisted transcript
parts.

Clients reconnect with bounded exponential backoff and jitter. Every new socket
starts empty, so the first operation is a complete `subscription.set`; no client
relies on a prior connection ID or server-restored state. For both initial
connection and every reconnect attempt, `mo` resolves the credential immediately
before creating the `ClientWebSocket`. Existing machine-local credentials remain
restricted to loopback endpoints. If an upgrade returns `401`, `mo` refreshes
the stored session at most once for that connection attempt, persists any token
rotation, resolves the credential again, and retries the upgrade once. Another
`401` ends that attempt and enters normal bounded backoff; it does not start a
refresh loop. `mo event tail` sets only `domain`, maps its type and match flags
to `domain.types` and `domain.match`, and resumes printing new events after
reconnect. CLI is the only client surface that accepts arbitrary domain type or
match selections. It does not claim that events during the gap were observed.

After each successful initial or reconnect `subscription.set` response, Web
invalidates and refetches every active query for that Project's domain data. It
also invokes the authoritative rendered-transcript refetch for every registered
active session and refetches every registered task-log scope. For each transcript
registration, the returned render replaces the persisted base snapshot. Raw
transcript notifications received from the current socket generation while that
HTTP read is in flight are buffered and then reapplied in receive order after
the replacement; subsequent notifications continue applying to that base. This
closes the read race within the new socket generation. It does not promise to
recover pre-persistence raw events lost during disconnection, and it does not
merge or deduplicate raw and persisted shapes by `id` or `sequence`. Task-log
entries reconcile by `seq`. Reconciliation runs even if no later notification
arrives. A `1013` close is treated exactly like any other gap and follows the
same reconnect-and-reconcile path.

Each reconciliation pass uses fixed transcript and task-log registration
snapshots captured before buffering begins. A replacement registration created
while that pass is in progress is not refetched by the old pass, so its direct
notifications cannot be overwritten by stale HTTP completion.

### Lifecycle and Ownership

One live-event owner under `AuthGate` always owns exactly one physical connection
for the active Project while mounted. Its wire subscription always contains the
canonical Web `DOMAIN_EVENT_TYPES` set in `domain.types`, with
`domain.match: null`, and `TRANSCRIPT_EVENT_TYPES` in `transcript.types`.
Components cannot register domain types or match expressions, and transcript
reconciliation registrations do not alter the transcript wire types.

The only dynamic registration APIs are
`registerTaskLogScope(workflowRunId, taskId)` and
`registerTranscriptReconciliation(sessionId, runtimeSessionId, refetch)`.
Transcript registration returns an idempotent dispose handle. The Web owner
admits at most 128 unique task-log scopes. `registerTaskLogScope` returns an
explicit failure and no dispose handle for a 129th unique scope, leaves the
aggregate wire subscription unchanged, and the caller uses the authoritative
HTTP polling/read path without live deltas. An already admitted
`(workflowRunId, taskId)` remains reference-counted and returns an idempotent
dispose handle even while 128 unique scopes are active, so duplicate
registrations do not consume another slot or unsubscribe each other.
Transcript reconciliation registrations identify the active rendered transcript
and supply its authoritative refetch operation; they affect reconciliation only.
Components never send `subscription.set` or replace connection state directly.
The owner serializes complete `subscription.set` snapshots and coalesces changes
that arrive while one is in flight, sending only the latest complete snapshot
after its response. The same owner performs reconnect and the post-ack
reconciliation defined above.

The Server socket registry owns physical connections, immutable Project scope,
atomic subscription state, bounded output, and close fencing. The existing
domain bus handler, `ITranscriptEventPublisher`, and `ITaskLogDeltaPublisher`
remain transport-neutral producers and publish into that registry. Runtime
transcript and task-log data do not enter the domain event bus.

Normal client shutdown sends WebSocket close `1000`. Server cancellation and
deployment drain stop accepting subscriptions, close connections, and discard
their state. A late publisher snapshot may target a closing connection; enqueue
failure is a best-effort drop and cannot revive it. No disconnected connection
state survives process or silo restart.

## Non-Goals

- The live socket has no durable subscription, replay cursor, acknowledgement,
  or durable queue.
- A socket observes one Project only; it cannot provide cross-Project events.
- Event payload `data` is not a routing surface.
- Event persistence and delivery retries belong to
  [`eventbus.md`](eventbus.md), not this protocol.

## Dispatcher and Consumer Relationship

The single dispatcher in `eventbus.md` serves two consumer types through the
same event vocabulary:

- **System consumers**: `[Subscription]` handlers registered at compile time.
- **User consumers**: Agent routing tables in `event-routing.md`.

A system handler must not receive an event that no user expression can
subscribe to.


