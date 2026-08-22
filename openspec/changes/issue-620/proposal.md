## Why

A failed Slack-originated execution currently closes liveness with only a ⚠️
reaction: unless the Agent explains itself through the reply action, the user
receives no failure notice and no recovery path other than reconstructing and
resending the original request. The signed interaction boundary (Stop), Block
Kit delivery, and the durable provider inbox / outbox primitives are now in
place, so failed results can carry an authorized, replay-safe Retry control
that starts one fresh execution attempt without weakening Stop's guarantees.

## What Changes

- Render a Server-owned failure notice for failed Slack-origin turns (initial
  launches and follow-ups) as the terminal explicit-failure projection,
  replacing today's reaction-only closeout: readable, sanitized failure facts
  (reason, category, next step). Agent-authored reply bodies stay with the
  reply action; the notice owns only the failure presentation and its
  recovery control.
- Attach an expiring, signed, actor- and context-bound `Retry` action to the
  failure notice only when the failure is retryable per an authoritative
  transient-failure category matrix (for example runner-unavailable,
  runner-lost, report-timeout, timeout/deadline, transport, rate-limit
  categories). Category-less, unknown, legacy, and input / configuration /
  permission failures stay readable but text-only; retryability is never
  inferred from failure text.
- Handle Retry clicks at the existing interaction boundary: verify signature
  (constant-time), expiry, freshness, and Connection / workspace /
  conversation / message binding under the validated adapter lease, then
  re-evaluate the actor through the current Connection access policy — a
  bound actor alone is not authorization. Invalid, tampered, expired, stale,
  replayed, or unauthorized clicks produce an explicit user-visible outcome
  with no execution side effect.
- An accepted Retry starts exactly one fresh execution attempt from the
  original Slack request context. A failed root request re-launches with a
  new Session, initial Input, and Turn under a retry-specific idempotency
  key; a failed threaded turn admits a force-new-turn follow-up in the
  original Session under that key, never joining an unrelated queued
  follow-up. The failed attempt remains immutable history, and the retry
  never targets another Connection or conversation.
- Persist a durable retry-operation record keyed by the signed action
  identity and commit it before any dispatch. Repeated delivery of the same
  action, Slack redelivery, adapter failover, concurrent clicks, and a Server
  crash between commit and dispatch converge on one attempt; a fixed-key
  recovery reminder resumes committed-but-pending operations, and interaction
  replays re-enter the same operation instead of returning only a duplicate
  receipt.
- Validate authoritative terminal state before dispatch: only a still-failed,
  not-yet-retried, resolvable target is accepted; results explicitly report
  accepted, already applied, stale, or unavailable.
- Deliver every Retry outcome through the existing outbox: an accepted retry
  acknowledges the new attempt and projects its working state (including the
  existing signed Stop control where applicable); rejected, stale,
  unavailable, already-applied, and replayed results remove or replace the
  obsolete Retry control.
- Extend the terminal delivery contracts so initial-launch and follow-up
  failure events carry the session / input / turn identity and failure
  category needed to authorize a retry deterministically; legacy events
  without those facts remain renderable but expose no Retry control.
- Keep the adapter a pass-through: it forwards the new action id and signed
  value and delivers Server-provided blocks unchanged (TypeScript adapter and
  the Go transport port), acknowledging Slack promptly as it does for Stop.

## Capabilities

- `slack-failure-retry-action`: The signed Retry control on failed Slack
  results — failure-notice presentation, the authoritative retryability
  matrix, action signing / expiry / context / actor verification with current
  access re-authorization, explicit click outcomes, durable presentation
  updates, and adapter pass-through.
- `slack-retry-execution-attempt`: The recoverable new execution attempt —
  retry-keyed fresh-attempt identity (root re-launch vs. force-new-turn
  threaded follow-up), the durable retry operation committed before dispatch,
  idempotency across replay, redelivery, failover, concurrency, and process
  restart, terminal-state validation, and immutability of the failed history.

## Impact

- **Server Slack control and presentation:** `SlackTurnControlService` gains
  the Retry action payload, signing, verification, and authorization
  branches; `SlackInteractionRoutes` dispatches Retry clicks and persists
  result presentations; `SlackTerminalDeliveryHandler` and
  `SlackStatusProjection` render the failure notice with its Retry blocks and
  terminal projections.
- **Session and launch contracts:** `AgentSessionGrain` / session domain gain
  a force-new-turn follow-up admission mode and an operation-targeted
  follow-up dispatch; `IAgentLauncher` / launch coordinator gain a
  retry-keyed launch with pre-minted identities, reusing the same
  application boundary a CLI or Web surface would call rather than a
  Slack-only path.
- **Durable state:** a new Slack retry-operation store with its database
  migration retains the action key, retry dispatch key, pre-minted attempt
  identities, pending / outcome state, and recovery lease; a fixed-key
  recovery reminder grain resumes committed operations. Terminal delivery
  events gain identity and failure facts additively; legacy events render
  without Retry.
- **Adapter contract:** `packages/mohist-slack` and the Go transport port
  (`packages/go/mohist-slack`) need only contract coverage proving the new
  action id / value and blocks pass through unchanged; no new adapter
  grammar, endpoint, or authorization logic.
- **Security and dependencies:** HMAC signing stays bound to the verified
  Connection credential with expiry, workspace / conversation / actor checks,
  and constant-time verification; no new third-party dependency. Stop
  behavior, Agent reply ownership, and Workflow recovery semantics are
  unchanged.
