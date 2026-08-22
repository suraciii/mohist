# Design: Signed Slack Retry Action and Recoverable New Attempt

## Context

Today a failed Slack-launched Turn leaves the user with reaction-only terminal
feedback (`SlackTerminalDeliveryHandler` → `FinalizeLivenessAsync`) and no
recovery affordance; re-typing the request loses provenance and audit linkage.
The failure facts that would justify a retry already exist: the Turn records
`AgentTurnResult.FailureCategory`, terminal delivery events carry it
(`AgentJobLineage.BuildTerminalDeliveryEnvelope`), and the reviewed Stop action
(`SlackTurnControlService` + `SlackInteractionRoutes`) established the pattern
for a Server-signed, operator-bound, five-minute button on the same interaction
route.

Existing machinery this design builds on:

- **Stop action** — HMAC-SHA256 over a canonical payload string keyed by the
  Connection's bot token, constant-time compare, five-minute expiry, actor
  binding to the session initiator, acceptance revalidation, provider-inbox
  click dedup, outbox `UserAction` reply keyed by `SHA256(actionValue)`.
- **Launch** — `IAgentLauncher.LaunchConnectionAsync` funnels every Connection
  launch through `AgentLaunchCoordinatorGrain` keyed by
  `slack:{team}:{conversation}:{messageTs}`, with pre-minted
  session/input/turn ids; replays resolve to the same identities.
- **Follow-ups** — ingress creates a queued turn + lease via
  `AcceptFollowupAsync` (idempotency key, pre-minted input id);
  `AgentSessionFollowupDispatcher.DispatchNextAsync` dispatches the next
  eligible queued turn through `BeginNextFollowupDispatchAsync`.
- **Obligation workers** — `SlackAgentAppBindingObligationWorker` shows the
  BackgroundService pattern (interval scan, scope per pass, catch-all logging).

Constraints: Stop behavior must not change; the Slack adapter needs no new
contract (`block_actions` forwarding is generic over action ids); the provider
inbox keeps owning only raw Slack message ingress; the failed Turn is an
immutable fact. Failure categories are free-form strings derived from runner
output/error codes (`AgentJobGrain.FailureCategoryFrom*`), so classification
must be an exact allowlist, never text heuristics.

## Goals / Non-Goals

**Goals:**

- One authoritative failure-category retryability allowlist shared by
  presentation and acceptance.
- A signed, operator-bound, five-minute Retry button on retryable failure
  notices, delivered through the existing interaction route next to Stop.
- A durable Retry operation (persisted before dispatch) that is the single
  source of truth for click idempotency, crash recovery, and at-most-one new
  attempt across concurrent clicks, redelivery, adapter failover, and restart.
- New attempt creation from original durable Slack provenance: root retry →
  new Session; thread retry → explicitly targeted follow-up in the original
  Session; failed Turn untouched.
- One shared retry application service that the button dispatches to — no
  second command grammar.

**Non-Goals:**

- Multi-Bot interaction selection (issue #634); stale draft PR #635 is not
  reused.
- Automatic retry / backoff policies; changing what failure facts AgentJob
  reconciliation records. (Reporting the runtime error kind as
  `failureCategory` on terminal follow-up events *is* in scope — thread
  retryability needs that fact at the source; see Decision 3 / T-007.)
- Manager DM conversations (`SlackDeliveryOwnerIds.ManagerProjectId`): their
  terminal presentation stays reaction-only; no Retry button there (see Open
  Questions).
- Rebinding existing thread→Session mappings when a root retry creates a new
  Session; the failed Session keeps its bindings and both Sessions stay
  independently observable.
- Shipping CLI/Web retry HTTP routes (the shared service lands first; routes
  join later without service changes).

## Decisions

### 1. One classifier over the recorded vocabulary, placed with the retry service

A static `AgentSessionRetryPolicy.IsRetryable(string? category)` holds the
allowlist. Exact ordinal comparison; absent, empty, or unknown values are not
retryable. It lives next to the retry application service (Agent/Sessions
services), not in the Slack folder, because both the Slack presentation and the
provider-agnostic acceptance path consume it.

The allowlist is defined over the failure-category vocabulary the system
actually records — not an invented parallel vocabulary — so every transient
class the issue names has a token a real producer emits:

| Allowlist token | Recorded by | Issue family |
| --- | --- | --- |
| `runner-unavailable` | Server reconciliation (`AgentJobFailureReasons.RunnerUnavailable`) | runner unavailable |
| `runner-lost` | Server reconciliation (`AgentJobFailureReasons.RunnerLost`) | runner lost |
| `report-timeout` | Server reconciliation (`AgentJobFailureReasons.ReportTimeout`) | report timeout |
| `deadline-exceeded` | Runner OpenCode turn deadline (`RuntimeError` kind, verbatim) | deadline/timeout |
| `timeout` | Runner Pi deadline, renamed by `mapPiErrorKind('deadline-exceeded')` | deadline/timeout |
| `generation-drain-timeout` | Runner OpenCode generation drain | deadline/timeout |
| `unavailable-runtime` | Runner turn-time runtime unavailability (both runtimes) | runtime transport unavailable |
| `runtime-unavailable` | Runner dispatch preflight — runtime missing or not ready | runtime transport unavailable |
| `rate-limited` | Reserved — no producer today | rate limited |
| `probe-timeout` | Reserved — no producer today | probe timeout |
| `retry-safe` | Reserved explicit opt-in marker — no producer today | explicit retry-safe |

How runner kinds reach the Turn: `AgentJobGrain.FailureCategoryFromErrorCode`
copies the runner's mapped `RuntimeError` kind verbatim
(`agent-job-turn.ts`: OpenCode kinds pass through `mapOpenCodeErrorKind`
unchanged except `unsupported-execution-configuration` →
`unsupported_execution_configuration`; the Pi path renames `deadline-exceeded`
→ `timeout` and `missing-session` → `runtime-session-missing` via
`mapPiErrorKind`, and dispatch preflight records `runtime-unavailable` /
`incompatible-execution-configuration` directly). Recorded permanent categories
that stay non-retryable include `invalid-input`, `permission-required`,
`incompatible-runtime`, `incompatible-execution-configuration`,
`unsupported_execution_configuration`, `missing-session`,
`runtime-session-missing`, `conflict`, `interrupted`, `turn-failed`,
`manager-credential-expired`, `workspace-unavailable`, `context_exhaustion`,
`context_exhaustion_suspected`, and `unknown`.

`workspace-unavailable` (routed-launch preflight) is deliberately outside the
allowlist: the issue does not name it and unknown ⇒ not retryable is the safe
default; admitting it later is a deliberate allowlist change. The classifier
tests pin the real recorded strings — referencing the
`AgentJobFailureReasons` constants directly (so a renamed server-side reason
breaks the test) and the runner kind strings — never the allowlist's own
re-typed tokens, so vocabulary drift between producers and the allowlist is
caught by tests instead of silently losing Retry buttons.

*Alternative:* a Slack-local copy — rejected: the spec forbids divergent
presentation/acceptance copies, and a CLI/Web retry needs the same definition.

### 2. Retry action payload mirrors Stop, sharing the signing helper

A new `SlackRetryActionPayload` record mirrors `SlackStopActionPayload`:
version `v1`, action name `retry`, Connection/Session/Turn ids, the Slack
conversation + message + thread identity it was rendered for, the bound
operator Slack user id, nonce, expiry, signature. The bound operator is the
session initiator (the initial launch provenance member), the same binding
Stop uses — so acceptance can enforce "exact bound member AND still Owner or
initiator".

Signing reuses the Connection's bot-token HMAC-SHA256 with the same canonical
string + `CryptographicOperations.FixedTimeEquals` verification. The
sign/verify helpers are extracted from `SlackTurnControlService` into a small
shared internal helper both services call; Stop's external behavior,
signature, and outcomes are untouched. Unavailable signing material suppresses
the button (no action rendered, notice keeps its plain presentation).

*Alternatives:* a dedicated per-Connection signing secret — rejected: adds a
rotation/provisioning surface for zero benefit; a JWT-style library — rejected:
the canonical-string HMAC is already the reviewed pattern.

### 3. Failure notice rendering at the terminal projection

When a terminal delivery event (initial launch or follow-up) settles a Turn as
`failed` with a retryable recorded category, `SlackTerminalDeliveryHandler`
additionally enqueues an explicit failure notice through
`SlackStatusProjection.EnqueueTerminalAsync` (promoting the replaceable
progress row in place) with blocks carrying the Retry action. Non-retryable,
absent-category, non-failure, and Manager deliveries keep exactly today's
reaction-only finalization.

Two payload gaps must close for this to work:

- `SlackTerminalDelivery` gains explicit `sessionId`/`turnId` fields (additive;
  the follow-up `jobKey` currently encodes them, the initial-launch key does
  not — no reverse lookup from jobKey should be needed at render time).
- `AgentSessionGrain.TryEmitFollowupDeliveryAsync` currently nulls
  `failureReason`/`failureCategory`; it starts populating them from
  `turn.Result`. But the root cause is one layer deeper and producer-side:
  the runner's follow-up terminal events carry only `failureReason`
  (`followup-handler.ts` → `recordFollowupActivity`; the only category ever
  recorded is `unknown` for expired manager credentials), and follow-up turns
  never pass through `AgentJobGrain`, so no server-side reconciliation
  supplies a category either. A runner-side task (T-007) therefore reports the
  failing runtime's error kind as `failureCategory` on terminal follow-up
  `session.activity` events, applying the same kind→category mapping the
  AgentJob path applies (Decision 1). The Server already consumes the field
  end-to-end (`ResolveFollowupTurnResult` → `turn.Result` → delivery
  envelope), so no additional Server change is needed. Absent facts — runners
  predating the change, failures with no recoverable error kind — still
  degrade to no button; that degradation is the safety net, not the permanent
  behavior.

*Alternative:* post a brand-new failure message instead of promoting the
progress row — rejected: duplicates the established in-place terminal pattern
and adds message noise next to the Agent reply action's own text.

### 4. Route dispatch by action id; Stop untouched

`SlackInteractionRoutes` keeps its envelope (operator auth, adapter lease
validation, Connection resolution, disabled-Connection rejection, outbox
`UserAction` reply) and dispatches by action id: `mohist_stop_turn` →
`SlackTurnControlService` (unchanged), new `mohist_retry_turn` → new
`SlackRetryActionService.HandleAsync`. The outbox reply keeps the
identity-stable `ActionDispatchRef` (hash of the signed action value), so
redelivery of the same interaction updates the same reply.

*Alternative:* fold Retry into `SlackTurnControlService` — rejected: couples
two operations and risks the "Stop unchanged" invariant for no gain.

### 5. One shared retry application service

`AgentSessionRetryService.RetryAsync(command)` is the single retry operation
surface: input is `(projectId, sessionId, turnId, idempotencyKey)`; output is
the recorded operation result. It owns, in order:

1. Re-read the target Turn's current failure facts and re-apply
   `AgentSessionRetryPolicy` → reject `no_longer_retryable` (this is the
   authoritative re-evaluation; the Slack layer does not duplicate it).
2. Decide root vs thread from the Turn's `IsLaunchTurn`.
3. Pre-allocate the execution identity (stable tokens derived from the
   operation id).
4. Claim-or-create the durable operation record (Decision 6) — this is the
   click idempotency point.
5. Create the attempt (Decision 7) and record the result.

The Slack button passes `idempotencyKey = action nonce`; future CLI/Web routes
pass their own keys and get records of the same shape with the same
invariants. Rejections before step 4 create nothing.

### 6. Durable operation store: new table, persist before dispatch

A new `agent_retry_operations` table (EF migration; `MohistDbContext`) holds
one row per accepted retry: operation id (PK), idempotency key (unique),
target session/turn ids (unique together — at most one retry operation per
failed Turn ever), kind (root/thread), pre-allocated execution identity, state
(`Pending → Finished`), recorded result state/text, timestamps. A small
`AgentRetryOperationStore` wraps claim-or-create on the two unique indexes.

The record is committed **before** any dispatch; the dispatch path requires a
committed pending record. Concurrent clicks race on the unique indexes: one
wins, the loser reads the winner's recorded result and returns it — same text,
one attempt. The provider inbox is not involved.

*Alternatives:* reuse `SlackProviderInboxStore` — rejected: the spec assigns
click idempotency to the operation receipt and keeps the inbox raw-ingress
only; Orleans grain storage — rejected: crash recovery and bounded cleanup want
a plain queryable row, matching the inbox/outbox precedent.

### 7. Attempt creation reuses launch / follow-up pipelines idempotently

- **Root retry** — rebuild `ConnectionLaunchOrigin` from the failed Turn's
  initial input provenance and call `LaunchConnectionAsync` with the
  pre-minted session/input/turn ids. `LaunchConnectionAsync` gains an optional
  idempotency-key override: the retry passes `agent-retry:{operationId}`
  because the default key (`slack:{team}:{conversation}:{messageTs}`) is the
  *original* launch key and would resolve back to the failed Session. The
  coordinator's replay semantics then make crash-recovery re-dispatch land on
  the same new identities.
- **Thread retry** — call `AcceptFollowupAsync` with the failed Turn's input
  text + provenance (already bound to the thread root), idempotency key
  `agent-retry:{operationId}`, and the pre-minted input id; then dispatch via a
  new `BeginFollowupDispatchForTurnAsync(turnId)` grain method — the body of
  `BeginNextFollowupDispatchAsync` selecting the *given* turn instead of the
  first eligible queued one, keeping the same executing/JobId guards. If the
  Session is busy, the new turn simply stays queued and the ordinary scheduler
  owns onward order; unrelated queued turns are never dispatched *by* the
  retry. The operation is `Finished` once the attempt is durably created
  (launch submitted / follow-up accepted), not when the runner acknowledges.

The failed Turn is never touched: status, reason, and category stay as
recorded, and the attempt carries only fresh identities.

*Alternative for thread retry:* force-dispatch past the executing guard —
rejected: breaks Session turn-concurrency invariants; queueing is the
Session's existing answer to a busy session.

### 8. Restart recovery and cleanup as an obligation worker

An `AgentRetryObligationWorker` (the `SlackAgentAppBindingObligationWorker`
pattern: ~1-minute interval, scope per pass, never throws) resumes every
`Pending` row by re-running the same idempotent dispatch (coordinator replay /
`AcceptFollowupAsync` idempotency + targeted dispatch) — no original click,
lease, or interaction needed, and the construction is idempotent so no second
attempt can result. The same pass deletes `Finished` rows past the retention
window (24 h) and never deletes `Pending` rows.

*Alternative:* per-operation Orleans reminders — rejected: the interval worker
pattern already exists, is simpler, and matches the inbox/outbox cleanup
precedent.

### 9. Acceptance checks and authorization

`SlackRetryActionService.HandleAsync` performs, in order: signature verify →
expiry → context match (Connection id, team id, conversation id) → actor
binding (exact bound operator) → current permission (operator is still
Connection Owner or session initiator, and the Connection's current access
policy does not deny the operator in that conversation, reusing the ingress
decider's policy read) → hand off to `AgentSessionRetryService`, which
re-checks target retryability. Disabled Connections are rejected earlier by
the route (existing behavior). Every rejection returns an explicit state +
user-facing text through the outbox reply and creates no execution resources.

Verification follows the repo tiers: pure classifier and payload sign/verify
matrices in UnitTests (L0); acceptance, operation-store idempotency, recovery
resume, targeted dispatch, and presentation specs in SpecTests with fake
stores and injectable `TimeProvider`; adapter packages only add the new action
id to existing forwarding tests.

## Risks / Trade-offs

- [Free-form category vocabulary drifts (runner-derived strings)] -> the
  allowlist is exact-match and defined over the recorded producer vocabulary
  (Decision 1), and its tests pin real recorded strings plus the
  `AgentJobFailureReasons` constants, so a renamed or newly added transient
  producer category fails a test instead of silently losing the Retry button;
  unknown ⇒ not retryable remains the default, and new transient categories
  still require a deliberate allowlist change, never a guess.
- [Follow-up terminal events currently carry null failure facts] -> the
  runner-side task (T-007) reports the mapped runtime error kind as
  `failureCategory` on terminal follow-up events, and Decision 3's
  server-side change forwards `turn.Result` facts into the delivery envelope;
  runners that predate the change and genuinely unknown failures still degrade
  to no button, which is always safe.
- [Second button for the same Turn (event redelivery re-renders a notice with
  a fresh nonce) could double-retry] -> unique (session, turn) index resolves
  any later click to the one recorded operation and its recorded result.
- [Committed operation whose interaction response was lost, or restart during
  dispatch] -> receipt-based recovery: worker or any later click returns the
  recorded result; identity pre-allocation plus idempotent pipelines guarantee
  one attempt.
- [Thread retry accepted while the Session is executing] -> the attempt turn
  queues and follows ordinary scheduler order; acceptance feedback says the
  attempt was accepted, not that it is running.
- [Root retry shares the original message identity with the failed Session] ->
  distinct idempotency key and pre-minted identities mint a genuinely new
  Session; old thread mappings intentionally stay on the failed Session.
- [Stop regression while extracting the shared signing helper] -> Stop's
  observable behavior is pinned by existing specs; the extraction is internal
  only and covered by them.
- [Rollback leaves orphaned operation rows] -> rows are inert without the new
  worker; cleanup resumes on redeploy.

## Migration Plan

1. Additive EF migration creating `agent_retry_operations` (no backfill; no
   existing table changes).
2. Server-side deploy: classifier, presentation, action service, application
   service, store, worker. The Slack adapter (TS and Go ports) needs no
   deploy-time change — `block_actions` forwarding is generic over action ids;
   only their tests gain the `mohist_retry_turn` id.
3. Rollback: redeploy the previous Server build. New rows stop being written;
  already-`Pending` rows stop dispatching (the user can re-issue the request
  manually); `Finished` history is inert. Dropping the table is optional and
  safe at any later migration point.

## Open Questions

- Manager DM failures: should Manager turns ever render Retry (they would need
  a Manager-side failure notice first), or is ordinary-Connections-only the
  permanent boundary?
- Retention window: 24 h proposed — is a shorter window enough given the
  five-minute action expiry makes late replays impossible?
- Should the Retry button also bind the Connection Owner as an alternate
  operator (two buttons or a looser binding), or is initiator-only the
  deliberate minimum?
- When CLI/Web retry routes land, do they surface the same
  `no_longer_retryable`/recorded-result states verbatim, or map them into
  their own error vocabulary?
