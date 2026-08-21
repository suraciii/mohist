---
status: converged
---

# Slack Adapter Go Port

This document defines the Go replacement for the Node adapter in
`packages/mohist-slack`. The adapter's behavioral contract — lease lifecycle,
delivery settlement, event normalization — is defined in
[`slack.md`](slack.md) and does not change. This document fixes the port's
mapping, the deliberate deltas from the Node implementation, and the rollout.
The Server and every other package are out of scope.

## Goals and Non-Goals

Goals:

- Replace the Node >= 22.19 runtime requirement with one static Go binary.
- Preserve the wire behavior at both boundaries: the Server HTTP contract and
  the Slack Socket Mode / Web API usage.
- Keep the OS service identity: `mohist-slack.service` on Linux and the
  Windows scheduled task keep their names; only the launch command changes.
- Pilot the Go toolchain for this repository before any larger port.

Non-goals:

- No Server, runner, web, or CLI behavior changes. The CLI's update and
  service-install stages change only the build and launch commands they issue
  for this adapter.
- No protocol changes: envelope shape, routes, lease semantics, and delivery
  outcomes stay identical.
- No release-artifact distribution channel. The adapter keeps building from
  source (`go build`) like Server and Runner do today; a prebuilt channel is
  deferred until the CLI itself migrates.

## Current Shape

```
text diagram
+--------------------+  Socket Mode (WSS)   +---------------------+
|      Slack         |<-------------------->|   adapter process   |
+--------------------+                      |  (one per machine)  |
                                            +----------+----------+
       chat.postMessage / update / files / reactions   | loopback HTTP
                                                       | {success, code, data}
                                            +----------v----------+
                                            |    Mohist Server    |
                                            +---------------------+
```

Six modules, about 2.4k source lines: `transport` (Server client),
`adapter` (per-target runtime state machine), `adapter-delivery` (payload
operations and reconciliation), `adapter-events` (normalization), `cli`
(process entry), `logger` (logfmt). About 3.3k test lines cover them through
injected fakes; the port keeps that seam structure.

## Server Contract

Unchanged. Restated here as the port's test oracle.

Every response body is a JSON object `{success, code?, data?}`. A response is
successful only when `success === true`; `data` may be `null`. The code
`lease_stale_or_expired` maps to a dedicated stale-lease error that drops the
runtime; it is never retried inline.

Requests carry `Authorization: Bearer <operator token>` and
`x-mohist-operator-id`. The base URL must be loopback (`localhost`, `[::1]`,
or `127.0.0.0/4`); the adapter refuses to start otherwise.

| Route | Purpose | Special null/error semantics |
| --- | --- | --- |
| GET `/api/slack-adapter/leases/targets` | discovery | invalid target kinds fail the cycle |
| POST `/api/slack-adapter/leases/acquire` | validation/runtime lease | `lease_not_acquirable` yields null, not an error |
| POST `/api/slack-adapter/leases/renew` | heartbeat renewal | `lease_stale_or_expired` yields null |
| POST `/api/slack-adapter/leases/hello` | app identity verification | three outcomes: verified, app_id_mismatch, lease_stale_or_expired |
| POST connection ingress or `/api/slack-manager/ingress` | message upstream | manager targets use the flattened body |
| POST connection interactions | block_actions upstream | connection targets only |
| POST deliveries claim | claim one delivery | null data means no work |
| POST deliveries claim-uncertain | claim an uncertain delivery | null data means no work |
| POST deliveries ack | settle a delivery | delivered, retry, or uncertain outcome |

## Ported State Machine

One independent runtime per target key
(`connection:{projectId}:{connectionId}` or `manager:{enrollmentId}`):

```
text diagram
discovered -> validation lease -> socket hello -> reportHello -> close probe socket
           -> runtime lease -> open socket + web client
           -> [heartbeat tick] renew lease --stale/id-changed--> evict runtime
           -> [delivery tick] drain
```

Invariants carried over verbatim:

- Generation fencing. Every await boundary re-checks that the runtime is still
  the map's value for its key and that its generation, socket, and web client
  are current; a superseded runtime's failures are swallowed, never acted on.
- Eviction only removes the map entry when the map still points at the evicted
  runtime. A stale error surfacing from a replaced runtime must never delete
  its successor.
- Drain single-flight. Concurrent drain triggers coalesce through a draining
  flag plus a requested flag; uncertain recovery runs before the claim loop,
  and the claim loop stops at the first null or at an uncertain outcome.
- Backpressure notice. An ingress result of `backpressured` posts the reason
  to the originating conversation before the event is acknowledged.
- Event acknowledgment order. Interactions acknowledge first, then forward.
  Messages forward first, then acknowledge; a failure between the two leaves
  the event unacknowledged so Slack redelivers.
- Token redaction. Error text written to logs has `xapp`, `xoxb`, `xoxp`, and
  `xoxe` token shapes replaced before emission.

One deliberate delta: the Node implementation gates concurrent events by
polling a counter every 5 ms until an in-flight slot frees up. The Go port
uses a buffered-channel semaphore of the same capacity (default 8). The
observable contract — at most N events processed concurrently per process —
is unchanged.

## Delivery Semantics

Acknowledgment outcomes are `delivered` (optionally with the provider message
identity), `retry` (with reason), and `uncertain` (with reason). Payloads are
JSON objects with an optional `operation`; unknown operations route to
reconciliation.

| Operation | Primary path | Degradation |
| --- | --- | --- |
| post_message (default) | statusDispatchRef looks up an existing message by client_msg_id in conversation history (limit 200) and updates it; otherwise posts, carrying thread_ts, client_msg_id, and blocks | API failure retries |
| chat_update | requires providerMessageIdentity | failure falls back to posting fallbackText when fallbackDispatchRef exists, otherwise retries |
| reaction_add / reaction_remove | reactions.add / remove | unsupported-error set (cant_react, message_not_found, not_in_channel, not_allowed_token_type, invalid_timestamp, channel_not_found, missing_scope): retarget once via statusDispatchRef, treat remove as delivered, post fallback on missing_scope when present, else delivered; all other errors retry |
| upload_file | filesUploadV2 with channel_id, or channels + thread_ts for threads | identity comes from share timestamps, else a history scan by file id; API failure retries |
| segments (>1) | sequential posts; only the first carries client_msg_id | any segment failure retries |

Reconciliation settles uncertain deliveries against provider state:
reactions via a full reactions.get compared against the intended operation;
messages via history matched on ts, client_msg_id, or fallbackDispatchRef. A
chat_update whose stored text differs retries with
`provider_mutation_absent`; a missing update with fallback material posts the
fallback instead.

Slack error codes normalize through one layer: response `error` fields first,
then the `API error occurred: <code>` message pattern. The Go port implements
the same normalization over `slack-go/slack` error shapes so every decision
table above sees identical inputs.

## Event Normalization

Message envelopes require the stable identity quadruple `api_app_id`,
`team_id`, `channel`, and `ts` (falling back to `event_ts`); a missing member
fails normalization and the event is not acknowledged. Direct-message
detection is `channel_type === "im"` or a `D`-prefixed channel id. Sender
classification checks bot markers first (`bot_id`, subtype `bot_message`,
`bot_profile`), then human (`user`), else unknown. Bot author metadata records
an identity conflict when event-level and profile-level app or bot ids both
exist and disagree. Mentions parse `<@ID>` references into a deduplicated
list. File refs survive only with id, name, mimetype, and a safe non-negative
size.

Interactions are `block_actions` only, accepted at the top level or wrapped in
an `interactive` payload whose value may be a JSON string. Required fields:
api_app_id, team id, user id, container channel and message timestamps,
interaction id (trigger_id, then action_ts, then event_id), action id, and
action value.

## Process Contract

Configuration comes from the environment:

| Variable | Default | Meaning |
| --- | --- | --- |
| ADAPTER_ID | `mohist-slack-{pid}` | adapter identity sent with leases and acks |
| SERVER_URL | `http://localhost:3456` | loopback base URL |
| MOHIST_OPERATOR_TOKEN / OPERATOR_TOKEN | none | direct operator token |
| MOHIST_OPERATOR_TOKEN_PATH | none | file fallback read as one token |
| MOHIST_OPERATOR_ID | `mohist-slack` | operator id header |
| SLACK_PROXY_URL | none | outbound proxy for Slack traffic |
| HEARTBEAT_INTERVAL_MS | 15000 | lease renewal cadence, floor 1000 |
| DELIVERY_POLL_INTERVAL_MS | 1000 | delivery poll cadence, floor 100 |
| DISCOVERY_POLL_INTERVAL_MS | 15000 | discovery cadence, floor 1000 |
| MAX_IN_FLIGHT | 8 | concurrent event gate |

Socket reconnects back off exponentially from 1 s, doubling to a 30 s cap.
SIGINT and SIGTERM abort the process context, stop all runtimes, and flush
logs.

Logs are logfmt on stderr: fixed prefix fields `time` (RFC 3339), `level`
(INFO or ERROR), `msg`, `service=slack`, `component`, then sorted call-site
fields with reserved keys dropped and logfmt quoting applied. The Go port
implements this as a custom `log/slog` handler so line format stays stable
for existing log consumers.

## Go Mapping

Module layout — flat on purpose at this size:

```
text literal
packages/go/mohist-slack/
  go.mod          module github.com/suraciii/mohist/packages/go/mohist-slack
  main.go         env config, signal handling, assembly
  serverapi.go    envelope client, nine routes, loopback guard
  adapter.go      runtime map, generation fencing, drain loops
  delivery.go     payload operations, fallback, reconciliation
  events.go       message and interaction normalization
  logging.go      logfmt slog handler, redaction
```

Library choices: `slack-go/slack` (socketmode and api packages) for Slack
connectivity, stdlib `net/http` for the Server client, `log/slog` for logs.
No other dependencies.

Concurrency mapping:

| Node construct | Go construct |
| --- | --- |
| AbortSignal | context.Context |
| setInterval timers | time.Ticker with explicit Stop |
| Promise.allSettled discovery fan-out | WaitGroup with per-target error collection |
| 5 ms in-flight poll | buffered-channel semaphore |
| WebSocket client | slack-go socketmode |

## Deltas from the Node Implementation

1. App identity verification. Node parses the raw Socket Mode hello frame for
   `app_id`. slack-go encapsulates the handshake, so the port verifies the
   presented identity with `auth.test` against the validation lease's
   expected app id during the probe phase. The guarantee — the connected app
   matches the lease expectation before a runtime lease is acquired — is
   identical and pinned by a contract test.
2. Proxied ping timeout. Node disables the client ping timeout (24 h) when an
   outbound proxy is configured. slack-go exposes different ping controls;
   Batch 3 must either match the behavior or document the difference and fail
   closed. This is the one open verification item.
3. JSON tolerance. Unknown fields are ignored; required-field validation
   errors are preserved exactly where the Node implementation raises them.
4. Discovery isolation. One failing target logs and skips; sibling targets
   proceed. Fan-out must not use an errgroup, whose cancellation would poison
   siblings.
5. Redaction. The token-shape redaction regex ports verbatim into the log
   handler path.

## Compatibility Commitments

- Server code and protocol: zero changes.
- `mo slack install*` credential staging: unchanged; the adapter still
  completes the Socket hello automatically after credentials are staged.
- Service names unchanged; installers swap only the ExecStart / launcher
  command from `node …/dist/cli.js` to the Go binary.
- Log line format preserved; log consumers keep working.

## Test Plan

| Node suite | Go suite | Focus |
| --- | --- | --- |
| transport.test.ts | serverapi_test.go | httptest Server replaying envelope, null, and error-code semantics |
| adapter.test.ts | adapter_test.go | fake transport/socket: lease transitions, stale eviction, drain coalescing, backpressure notice |
| adapter-delivery.test.ts | delivery_test.go | every operation's success, degradation, and reconciliation branches |
| adapter-events.test.ts | events_test.go | required-identity failures, string-wrapped interactive payloads |
| cli.test.ts | main_test.go | env resolution, token precedence, backoff sequence |
| logger.test.ts | logging_test.go | logfmt golden lines, redaction |

Time discipline follows the repository rules: injected clocks and tickers,
no wall-clock polling assertions. Before cutover, one end-to-end pass runs
against a real Slack app covering ingress, delivery, interaction, file, and
reaction paths.

## Rollout

| Batch | Content | Exit criterion |
| --- | --- | --- |
| 0 | this specification | merged |
| 1 | serverapi layer | contract tests green against fixtures |
| 2 | adapter core | fake-injected state-machine tests green |
| 3 | Slack integration incl. proxy ping verification | fake-socket behavior pinned; open item resolved |
| 4 | process entry | real-Slack end-to-end pass |
| 5 | deployment switch: CLI build stage, installer launch commands, CI job | `mo install/update/service slack` works end to end |
| 6 | delete the Node implementation | monorepo verify fully green without it |

Runtime leases are exclusive per target, so cutover switches whole
connections rather than shadow-running both implementations. Rollback is
redeploying the previous binary; no data migration exists on either side.
