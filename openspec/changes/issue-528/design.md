## Context

Issue 528 closes the "honesty under backlog and delivery uncertainty" contract for Slack Connections.
The #514 vertical delivered the store-level reliability layer — bounded inbox/outbox, dedup, replaceable
merge, the Degraded(Backpressured) flip, and the claim/uncertain/dead-letter state machine
(`SlackProviderInboxStore`, `SlackOutboxStore`, `SlackOutboxDispatcherService`,
`SlackConnectionHealthBackpressurer`). #517 added the unified diagnostic face. The product spec is
`docs/agent-connections.md`; the binding architecture is `design/slack-agent-connection.md`
("可靠性契约" / "实装差距").

What the contract still lacks is end-to-end honesty, in three places confirmed against the code:

- **Backpressure is one-way.** `SlackConnectionHealthBackpressurer.FlipBackpressuredAsync`
  (`Infrastructure/Slack/SlackConnectionHealthBackpressurer.cs:21`) only sets `Degraded` + a reason.
  Nothing clears it. A backpressured Connection stays Degraded forever — ingress stays rejected
  (`SlackConnectionRoutes.cs:1053` `IsBackpressured`) — until an operator disables/recreates it, even
  after the backlog fully drains. The dispatcher's three sweeps (`SlackOutboxDispatcherService.cs:68`)
  only advance rows; none reopens ingress.
- **Backpressured is invisible in the diagnostic.** `ConnectionDiagnostic.Compute`
  (`Agent/Services/ConnectionDiagnostic.cs:72`) has no Backpressured branch. A Degraded(Backpressured)
  connection with complete setup, valid credentials, online adapter and no identity drift falls through
  to the `Healthy` return (`:140`). The Web/CLI summary reports "operating normally" while ingress is
  refused — exactly the deception this issue must remove.
- **Backpressure rejection is a transport error, not a user-visible result.** `handleEvent` in the
  adapter (`packages/mohist-slack/src/adapter.ts:103`) calls `transport.ingress` and then `void result`
  (`:110`) — the adapter ignores `IngressResult`. Every existing rejection (empty prompt, agent
  needs-setup) is therefore server-enqueued as an outbox reply *before* the result returns. The
  backpressure path instead returns HTTP 409 (`SlackConnectionRoutes.cs:1054`), which the transport
  turns into a thrown error (`HttpAdapterTransport.read`, `transport.ts:59`); the sender sees nothing.
  This is also unsolvable via the outbox: when outbox overflow is the pressure, there is no capacity to
  enqueue the rejection reply.
- **Delivery uncertain is not surfaced.** `SlackOutboxStore.MarkDeliveryUncertainAsync` and the
  uncertain-timeout sweep produce `delivery_uncertain` rows, and `SlackOutboxStore.ListAsync` returns
  them — but no route, Web view, or CLI command exposes them, and there is no manual-resend path with a
  duplicate warning.
- **Long offline is not honest.** `RecordAdapterHeartbeatAsync` (`Slack/SlackSetupVerifier.cs:157`)
  overwrites `LastHeartbeatAt` unconditionally; `IsAdapterOnline` (`:162`) only derives a boolean. There
  is no comparison of the outage duration against Slack's finite event-retention window, so after a long
  reconnect Mohist silently resumes as if nothing could have been missed.

Constraints: Slack is at-least-once externally and Mohist does not claim exactly-once; the adapter is
stateless and the Server is the sole state/result authority; no real external dependencies or wall-clock
time in tests (`design/testing.md`).

## Goals / Non-Goals

**Goals:**

- Backpressure is reversible: a periodic sweep flips a Degraded(Backpressured) Connection back to
  Healthy once pending inbox and pending outbox counts are both below capacity, with no operator action.
- Backpressured is a first-class diagnostic state, distinct from Disabled / credentials / service
  offline / healthy, naming inbox-vs-outbox and a single wait/retry next step.
- A backpressure ingress refusal is visible to the Slack sender as "not accepted, retry shortly,"
  distinguishable from accepted-but-pending, even when the outbox itself is full.
- Delivery uncertain rows and their reasons are listable from the Connection page, CLI, and Owner
  diagnostic surface; manual resend warns about possible duplication and offers the authoritative result.
- Offline duration past Slack's event-retention window is detected at reconnect and surfaced as a
  "possible messages missed, resend critical delegations" notice — never auto-replayed.

**Non-Goals:**

- Persistent event/message caching inside `mohist-slack`, or any claim of exactly-once end-to-end
  delivery or indefinite Slack-side retention.
- Auto-replay of gap-window events in place of user resend.
- Changing the outbox state machine, the no-drop guarantees, the replaceable-merge semantics, or the
  "delivery state never overrides execution result" authority rule (already locked by #514).
- Credential rotation, owner transfer, Disable/Enable/Delete (already delivered by #517).

## Decisions

### D1. Backpressure recovery is a fourth sweep in the existing dispatcher grain

The `ISlackOutboxDispatcherGrain` is already the cluster-singleton reliability cadence, driven by a
persistent reminder (`SlackOutboxDispatcherGrain.cs:59`), serialized by the dispatch gate
(`SlackOutboxDispatcherService.cs:69`). Add a `RecoverBackpressureAsync` sweep alongside the existing
three, run from the same `DispatchAsync`. It queries Degraded(Backpressured), Enabled, non-deleted
Connections; for each, reads `SlackProviderInboxStore.CountPendingAsync` and
`SlackOutboxStore.CountPendingAsync`; when both are strictly below `InboxCapacityPerConnection` and
`OutboxCapacityPerConnection`, calls a new `RecoverBackpressuredAsync`.

- *Alternative — recover inline at ingress/claim time:* rejected. Ingress is refused while
  backpressured, so recovery cannot be gated on ingress. Recovery must proceed independently of whether
  anyone is sending; the periodic sweep is the only trigger that runs unconditionally.
- *Threshold — strictly-below capacity, no hysteresis:* overflow fires at `pending >= capacity`
  (`SlackOutboxStore.cs:85`, `SlackProviderInboxStore.cs:87`); recovery fires at `pending < capacity`.
  Symmetric and simple. *Considered:* a lower hysteresis threshold to avoid flapping at the boundary.
  Rejected because pending counts move in discrete delivery/dispatch steps and the merge dynamics make
  parking exactly at capacity across sweeps unlikely; hysteresis adds tunable surface for no real gain.

### D2. Flip-to-Healthy is reason-guarded on the backpressurer

Add `RecoverBackpressuredAsync(projectId, connectionId)` to `ISlackConnectionHealthBackpressurer`. It
`ExecuteUpdate`s `ConnectionHealth = Healthy`, `HealthReason = null` **only where the current
`HealthReason` is `InboxOverflow` or `OutboxOverflow`**. `Degraded` today is used only for backpressure
(credential/service failures use `Unhealthy`), but the reason guard future-proofs against a different
`Degraded` cause racing the sweep (e.g., a rotation path that sets a non-backpressure Degraded reason).
The recovery sweep's pre-check (both counts below capacity) plus this reason guard makes the transition
safe without a row lock on the Connection.

- *Alternative — flip via `AgentConnectionStore.UpdateAsync`:* rejected; that path is a generic
  field-bag update and would bypass the backpressure-specific invariant. The backpressurer owns the
  backpressure health transition in both directions.

### D3. Backpressured diagnostic branch, priority just below service-offline

Insert a `Backpressured` branch in `ConnectionDiagnostic.Compute` that matches
`ConnectionHealth == Degraded && HealthReason ∈ {InboxOverflow, OutboxOverflow}`, placed after the
service-offline check (`:103`) and before owner-unavailable (`:112`). Add
`ConnectionDiagnosticState.Backpressured`. Reason text names inbox-vs-outbox; next action is
"wait for the backlog to drain / retry input shortly." Placement rationale: backpressure is a
liveDegraded health condition on a service path that is otherwise up, more acute than the
configuration/choice states (owner, agent-needs-setup, disabled, identity drift) it precedes. This
guarantees a backpressured Connection can no longer fall through to the `Healthy` return (`:140`).

### D4. Visible rejection is rendered by the adapter, because the outbox may be the full side

Change the backpressure ingress path from HTTP 409 to a **success response carrying a structured
`IngressResult`** (`{ kind: "backpressured", reason: "…" }`), mirroring how empty-prompt and
agent-needs-setup rejections already return `{ kind: "rejected", reason }`. Then stop discarding the
result in the adapter: in `handleEvent` (`adapter.ts:103`), when `result.kind` is a user-facing
rejection kind, post `result.reason` to the originating conversation/thread via the already-held
`runtime.web` (`SlackWebClient`, the bot token) before `ack()`. This decouples rejection surfacing from
the outbox entirely, so it works when **outbox overflow** is the pressure — the one case where the
server cannot enqueue a reply.

- *Alternative — server enqueues the rejection reply into the outbox:* works only for inbox-overflow;
  unsolvable for outbox-overflow (no capacity) and is exactly the gap. Rejected as the primary path.
  (The server keeps enqueueing replies for the rejections it *can* enqueue; only backpressure — and any
  future can't-enqueue rejection — goes through the adapter-rendered path.)
- The adapter already possesses everything needed (`runtime.web`, the conversation id and `threadTs`
  from the normalized envelope), so this is a narrow change to `handleEvent`, not a new transport call.
  Ingress acceptance semantics are unchanged: ack still happens after the result is obtained, and only
  acceptance persists a `SessionInput`; a backpressured result acks without accepting.

### D5. Surface Delivery uncertain over the existing list; resend re-queues to Pending

Expose a `GET .../slack-connections/{id}/deliveries` route backed by the existing
`SlackOutboxStore.ListAsync` (returns state + reason per row); the Web Connection page and CLI render the
`delivery_uncertain` rows and their `LastError`. Manual resend calls a new
`SlackOutboxStore.ScheduleRetryAsync`-based endpoint that transitions the row `DeliveryUncertain →
Pending` (`ScheduleRetryAsync` already resets `NextAttemptAt` and bumps `AttemptCount`,
`SlackOutboxStore.cs:370`), so the adapter's existing claim→post→ack loop re-delivers it. The duplicate
warning is client-side confirmation in Web/CLI before the call (the original may secretly have landed);
the endpoint is the single transition.

- *Alternative — a dedicated resend that bypasses attempt accounting:* rejected; reusing
  `ScheduleRetryAsync` keeps one retry path and one attempt budget, so a resent uncertain row is still
  subject to `OutboxMaxAttempts` dead-lettering and does not live forever.

### D6. Offline-gap notice captured at heartbeat, persisted, cleared on proven liveness

Extend `RecordAdapterHeartbeatAsync` (`SlackSetupVerifier.cs:157`): before overwriting
`LastHeartbeatAt`, if an existing heartbeat exists and `now - existing >= SlackEventRetentionWindow`
(a new `SlackProviderOptions` knob), set a new nullable `AgentConnection.OfflineGapAt` to `now`. Add the
column via an EF migration. The diagnostic surfaces `OfflineGapAt` as a non-blocking notice ("messages
may have been missed during the outage; resend critical delegations") alongside the primary state. Clear
semantics: `OfflineGapAt` is reset to null when the **first new ingress is accepted** after the gap
(proven liveness — messages are flowing again) or on an explicit operator acknowledge. No events are
synthesized or replayed.

- *Alternative — derive the gap at read time from `LastHeartbeatAt`:* rejected; `LastHeartbeatAt` is
  overwritten on reconnect, so the outage duration is lost. The gap must be captured at the reconnect
  moment and persisted to survive reads.
- *Default for `SlackEventRetentionWindow`:* conservative (e.g., 30 min). Slack's Socket Mode redelivery
  window is short unless Delayed Events is enabled; the knob lets operators tune it. The notice fires
  only when the outage plausibly exceeded the window, avoiding false alarms on brief reconnects.

## Risks / Trade-offs

- [Recovery sweep flaps a Connection at the capacity boundary] -> Symmetric strictly-below/`>=`
  triggers plus discrete pending steps make steady-boundary parking unlikely; if it occurs, the only
  effect is the diagnostic label toggling — no accepted input or terminal row is touched (D2 is
  reason-guarded and read-checked).
- [Adapter-rendered rejection doubles up with a server-enqueued reply] -> Only the backpressure kind
  (and future can't-enqueue kinds) take the adapter path; existing server-enqueued rejections keep their
  path and are not rendered twice. The kind discriminator is the single source of truth.
- [Two reconnects in quick succession each stamp `OfflineGapAt`] -> Idempotent: setting it again to
  `now` is harmless; the notice is shown once and cleared on first accepted ingress regardless.
- [`OfflineGapAt` lingers if no ingress ever arrives post-gap] -> Operator acknowledge is the manual
  clear; the notice is non-blocking, so a lingering notice on an idle connection is honest, not harmful.
- [New `OfflineGapAt` column requires a migration] -> Additive nullable column; rollback is dropping it.
  No existing read depends on it.

## Migration Plan

1. Add the nullable `OfflineGapAt` column (`AgentConnection` + EF migration) — additive, no data backfill.
2. Ship server changes: reason-guarded `RecoverBackpressuredAsync` (D2), the recovery sweep (D1), the
   `Backpressured` diagnostic branch (D3), the structured backpressure `IngressResult` (D4 server side),
   the deliveries list + resend endpoints (D5), and the heartbeat gap capture (D6). All fake-time/fake-store testable.
3. Ship the adapter change (`handleEvent` renders rejection kinds; D4) — a behavior change to
   `mohist-slack`, rolled out via `mo update`.
4. Ship Web/CLI: Backpressured state rendering, uncertain-delivery list + resend-with-warning, and the
   offline-gap notice.
5. Rollback: the server changes are additive behaviors behind existing routes; revert restores the
   one-way backpressure and HTTP-409 rejection. The migration is additive (drop column to revert). The
   adapter change reverts to `void result`.

## Open Questions

- Exact default for `SlackEventRetentionWindow` and whether to surface it as a per-Connection diagnostic
  fact (so operators understand why a notice did/didn't fire). Proposed: 30 min default, not surfaced.
- Whether the offline-gap notice should also cover the Server-was-down case (Server restart with a stale
  `LastHeartbeatAt`) or only adapter-reported outages. Proposed: any heartbeat gap exceeding the window,
  regardless of cause — the honesty contract is the same.
