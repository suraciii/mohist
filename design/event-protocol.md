---
status: converged
---

# Event Protocol

This document defines Mohist's event envelope and its live delivery protocol.
Web and non-SignalR clients, including `mo`, use the same project-scoped native
WebSocket. The same router and expression language can subscribe to important
events from any entity. See
[`eventbus.md`](eventbus.md) for persistence and delivery and
[`event-routing.md`](event-routing.md) for the Agent-facing routing table.

## Three Orthogonal Axes

Every event envelope answers three questions through separate properties:
`type` says what happened, `source` says which entity emitted it, and context
extension attributes say which business lineage contains it.

`type` and `source` already have stable conventions. This protocol adds
mandatory **business-lineage context stamping**. It makes "subscribe to
everything under Issue #42" expressible as one predicate.

## `type`: Event Taxonomy

Types use `com.mohist.<domain>.<event>` and are registered in `EventCatalog`.
The Catalog answers only which stable event types exist. An event family and
its structure determine lineage requirements; the Catalog does not duplicate
an attribute schema for every type.

## `source`: Emitting Entity

The source uses the emitting entity's domain identity, such as
`/mohist/workflow-runs/{workflowRunId}`,
`/mohist/projects/{projectId}/issues/{issueNumber}`, or
`/mohist/projects/{projectId}/epics/{epicNumber}`. Project scope is part of an
Issue or Epic identity. Mutable business lineage such as Epic membership or
Workflow origin is not encoded into source.

## Context Attributes: Business-Lineage Stamping

### Rules

1. **Stamp completely at production time**: The store layer writes the flat
   extension attributes from lineage held by the producing aggregate at that
   moment. An Issue uses its own `EpicNumber?`; a WorkflowRun uses its Issue
   context. Stamping must not query another aggregate.
2. **Route by envelope only**: Matchers and dispatchers read only the envelope
   and never query the business domain. A domain reaction handler may read
   current aggregate state before issuing an idempotent command, but that read
   cannot change whether the route matched.
3. **Snapshot truth**: Attributes record ownership at production time. Moving
   an Issue to another Epic does not rewrite historical events.
4. **Admission criterion**: Promote a business identity to an envelope
   attribute when it is valuable as a routing dimension. Payload `data` never
   participates in routing.

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
syntax is a subset compatible with [CEL](https://cel.dev/). If later needs
outgrow the subset, a complete implementation can replace it without changing
stored expressions.

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

### Evaluator

The evaluator is a small internal implementation, estimated at 300-400 lines
plus a conformance suite, with no external dependency. `Cel` and `Cel.NET` are
not used because evaluation targets only a flat string-to-string dictionary,
does not need the CEL type system or protobuf integration, and neither library
is a mainstream community dependency.

## Live WebSocket Protocol

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

### Migration

1. Freeze shared JSON fixtures for `subscription.set`, all three notifications,
   JSON-RPC errors, and their exact nullability and casing. Add transport-neutral
   Project routing metadata to transcript and task-log publication where needed.
2. Implement the native socket registry, endpoint, domain bridge, and native
   transcript and task-log publishers behind the existing producer interfaces.
   The domain bridge embeds the object rendered by `WebhookPayloadRenderer`
   rather than introducing another envelope DTO. Keep the endpoint dormant while
   the SignalR path remains production-active.
3. Implement one Web connection owner with fixed canonical domain and transcript
   type sets, admission of at most 128 dynamic task-log scopes, and transcript
   reconciliation callbacks. It performs set-before-reconcile on connect. Set
   Vite's `/api` proxy to `ws: true`; it may preserve the browser's original
   Host. Configure Caddy's local HTTPS proxy to send one `X-Forwarded-Proto` and
   one `X-Forwarded-Host`. The endpoint uses those headers only from an immediate
   loopback peer, while direct development requests continue to use
   `Request.Scheme` and `Request.Host`. Adapt Web from the legacy SignalR
   camelCase envelope to the structured CloudEvents object. Implement the same
   JSON-RPC client in `mo event tail`, preserving NDJSON stdout and JSON selection
   while emitting the standard `params.event` object unchanged.
4. In one release candidate, activate the native publishers and both clients,
   then delete `MohistHub`, its SignalR event bridge and publishers,
   `ConnectionSubscriptionRegistry`, `IConnectionSubscriptionGrain`, SignalR
   event tests and package dependencies, and `/hubs/events`. Do not fan one
   publication through both carriers.
5. Delete `GET /api/projects/{projectRef}/events/tail`,
   `ProjectEventTailRoutes`, `IEventTailSource`, `EventTailSource`, their fakes,
   and the old CLI NDJSON HTTP reader after `mo` uses the socket. Remove the old
   tail projection with nested `extensions`; the native endpoint is the sole
   live event transport and no compatibility mode remains.

The migration preserves existing HTTP history, transcript, and task-log reads.
It does not add a durable event log, replay API, cursor, acknowledgement, custom
subscription language, or cross-Project observation socket.

## Dispatcher and Consumer Relationship

One router, the single dispatcher in `eventbus.md`, serves two consumer types
through the same protocol:

- **System consumers**: `[Subscription]` handlers registered at compile time.
- **User consumers**: Agent routing tables in `event-routing.md`.

See `eventbus.md` for how matching responsibilities differ between the two
surfaces. **Symmetry is the acceptance criterion**: the protocol is broken if a
system handler can receive an event that no user expression can subscribe to.

## Conformance

- `EventCatalog` maintains event types only and does not own another lineage
  matrix.
- Production rules are defined by aggregate event family. WorkflowRun, Issue,
  Epic, AgentSession, Runner, and Workspace each have required base context.
  Inbox-derived events inherit source-event context. Event structure, not a
  handwritten type list, decides whether to stamp `stage`.
- A spec suite traverses every real event-production path and asserts its
  envelope by producer family and event structure. Forgetting lineage on a new
  producer or emitted event fails the suite without an exception list.
- The expression evaluator has an independent conformance suite for syntax,
  missing attributes, regular-expression timeout, and determinism.
- Shared Server, Web, and CLI fixtures cover `subscription.set`, the three
  notification methods, JSON-RPC errors, exact JSON casing, nullability, and
  limits. The shared domain fixture is the CloudEvents 1.0 structured JSON object
  rendered by the same authoritative contract as outbound webhooks: lowercase
  core attributes, top-level extension attributes, and `data`. Renderer tests
  prove webhook bodies and `event.domain.params.event` are identical objects;
  no camelCase `CloudEventEnvelope` contract exists for the socket. Web fixture
  coverage asserts the intentional adaptation from the legacy SignalR shape,
  and CLI fixture coverage asserts unchanged NDJSON emission of the standard
  object and removal of nested `extensions`. Endpoint tests prove
  `IntegrationProjectConstraint` is enforced before `101`, cookie-authenticated
  upgrades require `Origin` to exactly match the canonical scheme and authority,
  and bearer upgrades succeed without `Origin`. They cover direct
  `Request.Scheme`/`Request.Host` matching; a loopback Caddy peer with one valid
  forwarded proto/host pair; rejection of malformed, multiple, or incomplete
  loopback forwarded values; and ignoring forwarded values from every
  non-loopback peer. Development configuration tests assert the `/api` Vite
  proxy enables WebSockets and may preserve Host; Caddy configuration tests
  assert its local HTTPS proxy sends the single forwarded pair. No test enables
  configurable trusted origins. Project-isolation tests prove that domain,
  transcript, and task-log publishers skip missing or mismatched Project
  metadata, including an `AgentSessionGrain` with no Project in session metadata.
- Connection tests prove idless client objects are ignored without execution or
  response but count as protocol errors; server notifications retain standard
  idless JSON-RPC notification semantics. They also cover duplicate in-flight
  IDs, malformed envelopes, unknown methods, invalid params, atomic replacement,
  and response-before-new-notification ordering under concurrent publication.
  Domain subscription tests prove arbitrary non-empty exact type strings are
  trimmed and accepted without catalog coupling, while empty or whitespace types
  are invalid and leave the prior state active. Transcript types remain catalog
  validated.
  The third-error tests assert an owed response is enqueued before close `1008`,
  while a failed enqueue closes `1013` without promising that response.
  Shutdown tests synchronize admission with `StopAsync` and prove both existing
  and late sockets are fenced; send-failure tests prove expected transport
  exceptions do not escape the upgraded request. Runtime match-failure tests
  assert both structured logging and the telemetry counter.
  Publisher tests prove transcript notifications carry explicit
  Project scope, are emitted before persistence, and expose only transient raw
  `id` and `sequence`. Client tests cover the single Web owner, its fixed
  reverse-DNS-only domain set and fixed transcript type set, admission of at most
  128 unique task-log scopes,
  explicit no-handle failure and unchanged aggregate for the 129th, HTTP polling
  fallback for its caller, duplicate-scope reference counting without another
  slot, idempotent dispose handles, serialized/coalesced complete snapshots, and
  unconditional post-ack reconciliation. Transcript tests hold the authoritative
  HTTP read open, receive current-generation raw notifications, replace the
  persisted base, and assert that buffered notifications are reapplied afterward
  without cross-shape sequence deduplication. They also replace a runtime
  identity during reconciliation, isolate a throwing task-log consumer, and
  prove transcript and task-log buffer overflow remains bounded and reconnects.
  A rejected authoritative read must clear buffering, close `4001`, and deliver
  normally again only after the replacement connection reconciles.
  CLI tests cover flag mapping, strict response and notification shapes, the
  standard event object, compact one-object-per-line NDJSON rendering, bounded
  peer-close acknowledgement, credential resolution and one-refresh `401`
  recovery, bounded output fencing, and removal of both legacy live endpoints
  after cutover.

## Status

Implemented: the three-axis envelope and event catalog; business-lineage
stamping with Lineage and ProducerConformance coverage for each production
path; the CEL-subset evaluator and user routing evaluation; promotion of the
`stage` attribute; and Workspace create and archive events
(`com.mohist.workspace.created` and `com.mohist.workspace.archived`) carrying
`workspace` and `workspaceoriginkind`. Dual Issue and Epic identities and the
old `issueid`, `epicid`, `issueno`, and `epicno` attributes are removed.

Implemented: the project-scoped native WebSocket live protocol and removal of
the SignalR and NDJSON live transports. The protocol fixes raw
transcript delivery before persistence, generation-local transcript
reconciliation, one fixed Web owner, canonical same-origin cookie upgrades, and
one CloudEvents 1.0 Published Language shared unchanged with outbound webhooks. Web
intentionally adapts from the legacy SignalR camelCase envelope, and
`mo event tail` emits the standard object as NDJSON without the old nested
`extensions` shape.
