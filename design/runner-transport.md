# Runner Control Transport

Mohist uses HTTP for Runner registration, heartbeat, work dispatch, result
reporting, volatile evidence delivery, and reconciliation reads. The native
WebSocket carries Server-to-Runner control. This document defines the transport
boundary and wire contract. It does not redesign work dispatch, Workspace
identity, or the domain event bus.

## System Boundary

```text diagram
+--------+ HTTP               +--------+
| Runner +------------------->| Server |
+--------+                    +----+---+
     ^              status notice  |
     +-----------------------------+
```

- HTTP carries registration, heartbeat, work poll, work reports, Runtime event
  delivery, task-log delivery, and reconciliation reads.
- WebSocket carries Workspace reads, Session commands, and the loss-tolerant
  Workflow status notification.
- WorkflowRun and AgentJob remain the work owners. The WebSocket does not carry
  work, replace HTTP poll/report, or become a second event bus.
- The control connection is authenticated, outbound from Runner, and singular
  per Runner. Mixed old and new control clients are unsupported.

## Preserved Boundaries

- WorkflowRun and AgentJob remain their own dispatch ledgers. Work stays
  pull-only through the existing HTTP poll and report protocol in
  [`runner.md`](runner.md).
- AgentSession remains the authority for Follow-up, Stop, Compact, Reset, and
  their stable operation identities. The transport does not create a generic
  command ledger.
- Runner keeps only process-lifetime coalescing for concurrent mutating
  controls. Server-owned operation admission and settlement decide whether a
  retry may apply an effect; Runner does not retain operation effect memory
  across restart.
- Workspace queries keep their domain inputs and results. This transport does
  not change Workspace or Repository cleanup.
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

Server applies heartbeat metadata repair only when the heartbeat connection ID
exactly matches the current control lease. A missing or stale connection ID
refreshes ordinary Runner presence only; it cannot change Runtime identity,
connection generation, registry metadata, or the current lease. Heartbeat never
registers or replaces a control connection. Only a successfully installed
WebSocket changes the current connection ID and generation.

Runner reconnects with bounded backoff and jitter. WebSocket Ping and Pong
detect a dead connection. They do not replace HTTP presence, heartbeat, or
Runtime readiness.

The Runner WebSocket client must support a Bearer header, the custom
`X-Runner-Connection-Id` upgrade header, and client Ping frames. Each attempt
uses a fresh canonical lowercase UUID. The production socket disables
compression, limits received messages to 4 MiB, and gives the HTTP upgrade 15
seconds.

Runner retries immediately, then after 2, 5, 10, and 30 seconds, repeating 30
seconds thereafter. Every non-zero delay receives independent plus-or-minus
20 percent jitter from an injected random source. The sequence resets only
after the first Pong on a connection. Merely accepting and then closing a
connection does not restore the immediate retry and cannot create a zero-delay
loop. Runner sends Ping every 15 seconds and fences a connection that does not
Pong within 10 seconds. Graceful close owns socket output for at most five
seconds before terminating the socket.

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

On Runner, the 64-message queue contains waiting JSON-RPC success and error
responses; the response currently owned by the writer is not in the queue.
Runner handlers have no transport timeout and Runner does not apply the
Server's 32-request in-flight cap. With one active send, 64 additional responses
are accepted; the next queued response fences the connection
and closes it with `1013`. The `ws.send` callback is the response completion
boundary. One output owner serializes data, Ping, and close so those operations
cannot race.

### Connection lifecycle

The endpoint and connection registry attach the current physical connection,
refresh Runner build and Runtime identity, and notify tracked AgentSessions
when the matching connection disconnects. A replacement supersedes the old
connection before it accepts requests. A late close from the old connection
must not unregister the replacement. This transport has one connection and one
presence model.

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

Runner counts every response-producing protocol failure toward the
per-connection threshold and closes with `1008` after the third. An unknown
notification method is ignored without a response and is not itself a protocol
error. A malformed envelope, or malformed params for the known notification,
is a protocol error even though notifications never receive responses.

If Runner receives a second live request with the same `id`, it returns
Invalid Request (`-32600`) without invoking the duplicate and without removing
or replacing the original pending operation. The error necessarily shares the
duplicate ID. Server may therefore treat either same-ID response as malformed
or unavailable; that is acceptable because duplicate live IDs violate the
profile and preserving the original operation is safer than transferring its
identity.

JSON object properties not named by a DTO are ignored, matching the default
System.Text.Json contract behavior. Required properties and all nested values
are still validated before a handler can produce side effects.

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

- `workspace.diff` takes `WorkspaceQueryParams` and returns `RunnerWorkspaceDiffResult?`.
- `workspace.commits` takes `WorkspaceQueryParams` and returns `RunnerWorkspaceCommitsResult?`.
- `workspace.commit-diff` takes `WorkspaceCommitDiffParams` and returns `RunnerWorkspaceCommitDiffResult?`.
- `workspace.status` takes `WorkspaceQueryParams` and returns `WorkspaceStatus`.
- `workspace.file-content` takes `WorkspaceFileContentParams` and returns `RunnerWorkspaceFileContentResult`.
- `workspace.remove` takes `WorkspaceQueryParams` and returns `WorkspaceRemovalResult`.
- `session.followup` takes `FollowupParams` and returns `RunnerFollowupDeliveryResult`.
- `session.stop` takes `SessionStopParams` and returns `RunnerStopReply`.
- `session.command` takes `SessionCommandRequest` and returns `SessionCommandResult`.
- `workflow.status-changed` takes `WorkflowRunStatusNotification` and returns none.

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
Invalid Params before invoking its handler. The transport has no fallback for
missing operation identities.

## Delivery Semantics

### Read requests

The five Workspace read methods are connection-scoped observations. Server
applies a timeout. A disconnect or lost response returns unavailable. Server
does not replay a read automatically; a caller may issue a new read.

Read handlers retain their existing path, Git argument, and process timeout
rules. The control connection applies one response-size limit and returns a
JSON-RPC server error when a result exceeds it. The transport does not change
what a Workspace read means.

### Mutating requests

`session.followup`, `session.stop`, and `session.command` carry the stable
business operation identity prepared by AgentSession. Runner coalesces live
concurrent duplicates in process memory only. Follow-up and Stop have no
Runner journal. AgentSession records Compact and Reset effect admission on the
Server before dispatch; a same-ID outcome-less admission is unavailable after
restart, while a new caller identity may retry on a validated replacement
process generation. Stop settles from the Server-recorded identity and the
Runtime witness for the target Turn.

A lost WebSocket response does not settle the business operation. AgentSession
keeps its current pending or unknown state and decides whether to redeliver,
query, or stop under its existing operation contract. The transport neither
stores that state nor converts a timeout into a domain result.

The Server control transport accepts an optional request-enqueued callback. It
invokes that callback exactly once after the request enters the connection's
write queue and before awaiting the response. The request timeout starts at the
same enqueue boundary. Stop delivery reports `DispatchStarted: false` when the
binding is incomplete, no live connection exists, or dispatch fails before
enqueue. Once the callback runs, timeout, disconnect, remote error, or protocol
failure returns no reply with `DispatchStarted: true`.

Server adapters map transport failures at their existing domain boundaries:

- caller cancellation always propagates;
- Follow-up logs unavailable, timeout, disconnect, remote, and protocol errors
  and returns `Accepted: false`;
- Session command maps transport, remote, and protocol errors to
  `SessionCommandError.Unavailable`;
- Workspace status, file content, and removal retain their existing
  `runner_unavailable` or domain fallback results; Workspace read RPCs retain
  their existing route-level unavailable behavior, and cleanup never exposes a
  remote transport exception as an HTTP 500; and
- Workflow notification failures are logged and dropped.

`workspace.remove` remains an idempotent, checkable local operation. Repeating
the same Workspace removal may report already absent. It must still use the
existing Runtime removal fence before deleting a directory. The transport adds
no Workspace removal aggregate or Runner journal.

### Notification

`workflow.status-changed` has no response and no replay cursor. Runner treats it
as a prompt to perform its existing HTTP Workflow status reconciliation. Losing
or duplicating the notification changes latency, not the cleanup decision.
It never marks a Workspace eligible directly; final host wiring maps the
notification callback to one status-convergence pass.

## Non-Goals

This transport does not add:

- a durable WebSocket event log, sequence, cursor, replay, or snapshot protocol;
- a generic Runner command ledger or operation poll;
- Nostr envelopes, subscription filters, or relay federation;
- a custom RPC envelope or additional `Sec-WebSocket-Protocol` negotiation;
- a second HTTP API for each WebSocket method;
- Workspace `homeEpoch`, checkout registration, or a new removal state machine;
- compatibility with old Runner control clients.

## Status

Native WebSocket control is active for all nine request methods and the
Workflow status notification. Runner opens the client after HTTP registration
and uses the transport-neutral handler catalog. SignalR control endpoints,
clients, handlers, test fakes, and dependencies are removed. The project-scoped
native event WebSocket replaces `/hubs/events`. HTTP registration, heartbeat,
work poll and report, Runtime event and task-log queues, and Workflow status
reconciliation remain unchanged. Runner keeps no operation journals; Server
identity and admission state survive Runner restart, while Runner effect memory
does not.
