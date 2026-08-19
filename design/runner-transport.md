# Runner Control Transport Migration

Mohist currently uses HTTP for durable Runner work and SignalR client-result
calls for Server-to-Runner control. This change replaces only the SignalR
control transport. It does not redesign work dispatch, Session operations,
Workspace identity, or the domain event bus.

## Migration Scope

The current boundary has two channels:

- HTTP handles Runner registration, heartbeat, work poll, work result report,
  Runtime event delivery, and reconciliation reads.
- SignalR handles five Workspace reads, four local mutations, and one
  loss-tolerant Workflow status notification.

The migration keeps the HTTP channel and every business owner unchanged. It
replaces SignalR with one authenticated, outbound WebSocket connection:

```text diagram
Before
  Server <---- HTTP poll/report ---------------- Runner
  Server <---- SignalR client results ---------- Runner

After
  Server <---- HTTP poll/report ---------------- Runner
  Server <---- native WebSocket control -------- Runner
```

The target removes the SignalR Hub, SignalR Runner client, and SignalR-specific
dispatch code. It does not keep SignalR as a fallback after cutover.

## Preserved Boundaries

- WorkflowRun and AgentJob remain their own dispatch ledgers. Work stays
  pull-only through the existing HTTP poll and report protocol in
  [`runner.md`](runner.md).
- AgentSession remains the authority for Follow-up, Stop, Compact, Reset, and
  their stable operation identities. The transport does not create a generic
  command ledger.
- Runner keeps its existing operation journals and operation-specific recovery
  rules. A transport retry never authorizes an unsafe effect replay.
- Workspace queries keep their current domain inputs and results. Named
  Workspace and Repository cleanup is a separate design change.
- Workflow terminal status remains a best-effort notification. Runner's HTTP
  status reconciliation remains the correctness backstop.
- Runtime events, logs, progress, readiness, and snapshots remain on their
  current HTTP paths. The WebSocket is not a second event bus.

## Connection

Runner registers through HTTP, then opens
`/api/runner/{runnerId}/control` with a Bearer credential that satisfies the
`runner` Scope. As with current Runner routes, a Runner-bound credential must
match the route identity; `operator` retains its existing Scope override. For
each connection attempt, Runner generates a new UUID and sends it in
`X-Runner-Connection-Id`. The identifier is not a credential; it names this
physical connection for replacement and poll readiness fencing. The header must
be the canonical lowercase D-format UUID string produced by `Guid.ToString("D")`
and must not equal any active WebSocket connection ID. Runner's
existing `buildGitHash`, `component`, `version`, `sourceRevision`, `treeHash`,
`artifactDigest`, `releaseId`, and `generation` query fields remain handshake
metadata on this endpoint.

Server keeps one current control connection for each Runner. A new connection
replaces the old connection. Request correlation belongs to one connection and
cannot cross a reconnect. Replacement immediately fences the old transport,
then closes it normally with reason `Replaced`. A stale close from the old
transport cannot unregister the replacement.
Replacement installation is serialized per Runner. A candidate does not fence
or otherwise supersede the current connection until it owns that Runner's
installation slot; cancelling a candidate waiting for the slot has no effect on
the current or installing connection. Connection ID reservations are also
owned: cleanup can release only the exact reservation it acquired.

Runner includes the current `X-Runner-Connection-Id` value in its existing HTTP
poll and heartbeat payloads. On upgrade, Server maps that public connection ID
to its existing process-local monotonic `connectionGeneration` and records the
generation with Runner runtime identity. For a matching poll, Server injects
the generation before dispatch. A nonmatching poll may reconcile already-held
work, but Server does not accept its Runtime readiness or give it fresh work.

At cutover, Server applies a metadata heartbeat only when its connection ID
matches the current lease. In the target behavior, heartbeat repair does not
register or replace a control connection; only a successfully installed
WebSocket changes the current connection ID and generation. Until that atomic
cutover, the existing SignalR heartbeat repair remains active.

Runner reconnects with bounded backoff and jitter. WebSocket Ping and Pong
detect a dead connection. They do not replace HTTP presence, heartbeat, or
Runtime readiness.

Each side uses one 64-message write queue shared by requests and notifications.
Queue saturation fences the connection and closes it with `1013`. Server permits
at most 32 in-flight requests per Runner; a 33rd request fails locally as
unavailable without closing a healthy connection. The current 15-second control
request timeout begins when a request is enqueued. A JSON-RPC text message is at
most 4 MiB, measured as the aggregate UTF-8 bytes in one fragmented text
message. An oversized message closes with WebSocket code `1009`. A disconnect
completes all pending transport requests as unavailable. These are protocol
constants, not deployment configuration. Socket sends and close output are
serialized. Fencing cancels an active send, waits at most five seconds to own
socket output, and gives `CloseOutputAsync` at most five seconds; either bound
expiring aborts the socket.

### Preserved Hub lifecycle

The WebSocket endpoint and connection registry replace transport code, but they
must preserve the current non-transport behavior of `RunnerHub`:

- attach the current physical connection through `RunnerConnectionTracker` and
  retain the connection generation used by HTTP poll admission;
- refresh the Runner's build and Runtime identity through the existing Runner
  lifecycle path; and
- on disconnect, unregister only the matching connection and notify every
  tracked AgentSession that its Runner disconnected.

A replacement connection supersedes the old tracker entry before it accepts
requests. A late close from the old connection must not unregister the new one.
This migration does not introduce a second connection or presence model.

## Wire Protocol

The control connection uses the single-object messages from
[JSON-RPC 2.0](https://www.jsonrpc.org/specification). JSON-RPC defines the
request, response, notification, correlation, and error envelopes; Mohist
defines only a WebSocket profile and the closed method catalog below.

Server request:

```json
{
  "jsonrpc": "2.0",
  "id": "req_123",
  "method": "session.stop",
  "params": {}
}
```

Runner response:

```json
{
  "jsonrpc": "2.0",
  "id": "req_123",
  "result": {}
}
```

Server notification:

```json
{
  "jsonrpc": "2.0",
  "method": "workflow.status-changed",
  "params": {}
}
```

Mohist's WebSocket profile is deliberately small:

- One UTF-8 WebSocket text message contains exactly one JSON-RPC object.
- Batch arrays are outside this profile. Mohist sends only single objects and
  rejects an array as Invalid Request.
- `params`, when present, is a named JSON object, never a positional array.
- Request `id` is a non-empty string unique among requests in flight on that
  connection.
- Server sends requests and notifications; Runner sends responses. Neither side
  adds methods in the opposite direction.
- Message size, in-flight request, queue, and timeout bounds apply before
  dispatch.

The JSON-RPC `id` correlates one request and response on one connection. It is
not a business idempotency key. A retry may use a new `id`, but mutating
`params` must retain the original `operationId` or other domain identity.

Protocol failures use the JSON-RPC 2.0 standard errors: Parse Error (`-32700`),
Invalid Request (`-32600`), Method Not Found (`-32601`), Invalid Params
(`-32602`), and Internal Error (`-32603`). Mohist does not define another wire
status enum. Expected domain outcomes such as conflict or not found remain in
the typed `result` models. A response that would exceed the connection's size
limit returns code `-32001` with message `Response too large`.

Malformed responses and duplicate live `id` values are protocol errors. A
response for an expired or otherwise completed request is ignored and logged;
normal timeout races must not close a healthy connection. The receiver closes
the connection after three protocol errors on that connection. A notification
never receives a response, including when its method or params are invalid.

The request methods are:

- `workspace.diff`
- `workspace.commits`
- `workspace.commit-diff`
- `workspace.status`
- `workspace.file-content`
- `workspace.remove`
- `session.followup`
- `session.stop`
- `session.command`

The only notification method is `workflow.status-changed`.

Every method has one named `params` object:

| Method | `params` DTO | Result DTO |
| --- | --- | --- |
| `workspace.diff` | `WorkspaceQueryParams` | `RunnerWorkspaceDiffResult?` |
| `workspace.commits` | `WorkspaceQueryParams` | `RunnerWorkspaceCommitsResult?` |
| `workspace.commit-diff` | `WorkspaceCommitDiffParams` | `RunnerWorkspaceCommitDiffResult?` |
| `workspace.status` | `WorkspaceQueryParams` | `WorkspaceStatus` |
| `workspace.file-content` | `WorkspaceFileContentParams` | `RunnerWorkspaceFileContentResult` |
| `workspace.remove` | `WorkspaceQueryParams` | `WorkspaceRemovalResult` |
| `session.followup` | `FollowupParams` | `RunnerFollowupDeliveryResult` |
| `session.stop` | `SessionStopParams` | `RunnerStopReply` |
| `session.command` | `SessionCommandRequest` | `SessionCommandResult` |
| `workflow.status-changed` | `WorkflowRunStatusNotification` | none |

JSON `null` is a valid result only for `workspace.diff`, `workspace.commits`,
and `workspace.commit-diff`. Every other request method requires a non-null
result matching its DTO. `workspace.status` additionally requires the `exists`
member; omission is a malformed result rather than `exists: false`.

`WorkspaceQueryParams` contains `query: RunnerWorkspaceQuery`.
`WorkspaceCommitDiffParams` contains `query` and `hash`.
`WorkspaceFileContentParams` contains `query` and `path`. `FollowupParams`
contains the current `target`, `text`, `operationId`, `inputId`, `turnId`,
`slackExecutionContext`, and attachment descriptor fields. `SessionStopParams`
contains the current `target`, `sessionId`, `turnId`, and `operationId` fields.

The nested wire values are:

- `RunnerWorkspaceQuery` has nullable `workflowRunId`, `projectId`,
  `issueNumber`, `repositoryName`, `gitUrl`, `workspacePath`, `branch`, and
  `baseBranch` fields.
- `target` is a discriminated object. Both kinds require `kind`, `projectId`,
  and `binding`. The `workflow` kind requires non-empty `workflowRunId` and
  `sessionName` and may carry `sessionId`; the `generic` kind requires a
  non-empty `sessionId` and may carry `definition`.
- `binding` contains `runtime`, `runtimeSessionId`, `runnerId`, and nullable
  `workDir`.
- Each Follow-up attachment contains `id`, `name`, nullable `contentType`, and
  numeric `size`. `slackExecutionContext` and `definition` retain their existing
  domain DTOs.
- Follow-up requires non-empty `turnId` and `operationId`; `inputId` remains
  optional. Stop requires non-empty `sessionId`, `turnId`, and `operationId`.

The JSON-RPC dispatcher rejects a mutating request missing these identities as
Invalid Params before invoking its handler. Current Server dispatchers already
supply them; the migration removes the Runner's legacy unjournaled fallback.

Step 1 promotes anonymous SignalR payloads to these named DTOs and freezes exact
JSON names, nullability, and enum spellings in shared fixtures. The carrier
wrappers above are new; nested domain values and result semantics do not change.
C# and TypeScript contract tests decode the same checked-in request, success, and error
fixtures for every method. No schema generator or generic map-based handler is
added.

## Delivery Semantics

### Read requests

The five Workspace read methods are connection-scoped observations. Server
applies a timeout. A disconnect or lost response returns unavailable. Server
does not replay a read automatically; a caller may issue a new read.

Read handlers retain their existing path, Git argument, and process timeout
rules. The control connection applies one response-size limit and returns
a JSON-RPC server error when a result exceeds it. This migration does not
otherwise change what a Workspace read means.

### Mutating requests

`session.followup`, `session.stop`, and `session.command` carry the stable
business operation identity prepared by AgentSession. Runner checks its
existing journal before applying an effect. Follow-up keeps its current
deduplication by `(sessionKey, operationId)` and does not add payload storage.
Stop and Session command retain their current request comparison and conflict
behavior. Each operation follows its existing reconciliation rule.

A lost WebSocket response does not settle the business operation. AgentSession
keeps its current pending or unknown state and decides whether to redeliver,
query, or stop under its existing operation contract. The transport neither
stores that state nor converts a timeout into a domain result.

`workspace.remove` remains an idempotent, checkable local operation. Repeating
the same Workspace removal may report already absent. It must still use the
existing Runtime removal fence before deleting a directory. This migration does
not add a Workspace removal aggregate or another Runner journal.

### Notification

`workflow.status-changed` has no response and no replay cursor. Runner treats it
as a prompt to perform its existing HTTP Workflow status reconciliation. Losing
or duplicating the notification changes latency, not the cleanup decision.

## Implementation Order

1. Introduce the named `params` DTOs and freeze shared JSON contract fixtures.
2. Implement the Server WebSocket endpoint, connection registry, correlation,
   timeout, bounded queues, connection-ID upgrade header, and preserved Hub
   lifecycle hooks. Keep the endpoint dormant and heartbeat behavior unchanged.
3. Implement the Runner WebSocket client and bind the typed methods to the
   existing handlers and journals.
4. In one release candidate, switch every Server control caller to the
   WebSocket registry and delete `RunnerHub`, the Runner SignalR client,
   recording SignalR test fakes, and unused SignalR package dependencies. In
   the same change, replace heartbeat registration with current-lease matching.
5. Cut over with a coordinated stop, deploy, and start of Server and Runner.
   Start Server before Runner. Mixed old and new control clients are unsupported.

Steps 1 through 3 do not change the production carrier. Each step must leave
HTTP work poll and report behavior unchanged. Production moves directly from
SignalR to WebSocket at steps 4 and 5 and never supports both protocols as a
runtime compatibility mode.

## Non-Goals

This migration does not add:

- a durable WebSocket event log, sequence, cursor, replay, or snapshot protocol;
- a generic Runner command ledger or operation poll;
- Nostr envelopes, subscription filters, or relay federation;
- a custom RPC envelope or additional `Sec-WebSocket-Protocol` negotiation;
- a second HTTP API for each WebSocket method;
- Workspace `homeEpoch`, checkout registration, or a new removal state machine;
- compatibility with old Runner control clients after cutover.

These features solve problems outside the SignalR-to-WebSocket migration. Add
one only when an independent product requirement needs it.

## Status

Server hosts the dormant native WebSocket endpoint and connection registry.
Production control callers still use SignalR client-result calls for the nine
request methods above and a SignalR send for the Workflow status notification;
no production Runner opens the WebSocket yet. While the endpoint is dormant,
the existing SignalR heartbeat repair behavior remains unchanged. It is removed
atomically with the production caller cutover, not in this phase. Existing HTTP
dispatch, result delivery, operation journals, and Workflow status
reconciliation remain the preserved baseline.
