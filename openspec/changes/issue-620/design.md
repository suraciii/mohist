## Context

The [proposal](proposal.md) describes the gap: a failed Slack-originated
execution currently closes liveness with only a ⚠️ reaction. Unless the Agent
explains itself through the reply action, the user gets no failure notice and
no recovery path beyond reconstructing and resending the request. The
[specs](specs) require a Server-owned failure notice with an authorized,
replay-safe Retry control, and a recoverable fresh execution attempt.

The primitives this change builds on are in place:

- **Signed actions.** `SlackTurnControlService` already signs the Stop action
  (`mohist_stop_turn`) with an HMAC over a canonical payload, key = the
  Connection's BotToken secret, constant-time verification, expiry, context
  binding, and an explicit user-visible outcome per click.
- **Interaction boundary.** `SlackInteractionRoutes` is the single adapter
  entry point: lease-validated envelope → service dispatch → outbox
  `UserAction` `chat.update` on the clicked message. The provider inbox
  deduplicates clicks by message identity (`action:{nonce}`).
- **Terminal delivery events.** Initial launches emit
  `com.mohist.agent.job.terminal-delivery` from the AgentJob grain (carrying
  failure reason/category but no session/input/turn identity); follow-up turns
  emit `com.mohist.agent.session.followup-delivery` from the AgentSession
  grain (carrying identity only inside `jobKey`, and always-null failure
  reason/category). `SlackTerminalDeliveryHandler` consumes both and today
  performs only the reaction closeout (`FinalizeLivenessAsync`).
- **Launch and follow-up boundaries.** Root requests launch through
  `IAgentLauncher.LaunchConnectionAsync` into the `AgentLaunchCoordinatorGrain`
  (idempotency key, persisted plan, pre-minted ids, replay-safe). Follow-ups
  enter `AgentSessionGrain.AcceptFollowupAsync` (idempotency key, steer or
  new-turn admission) and dispatch through `AgentSessionFollowupDispatcher` /
  `BeginNextFollowupDispatchAsync` (FIFO over queued turns).
- **Durable sweeps.** Cluster-singleton reminder grains
  (`SlackOutboxDispatcherGrain`, `EventDispatcherGrain`) show the fixed-key +
  persistent-reminder + startup-activation + post-commit-poke pattern.

Constraints that shape the design: the adapter must stay a stateless
pass-through (no new grammar or endpoints); Agent reply bodies belong solely
to the reply action; the retry must reuse the same application launch boundary
a CLI or Web surface would call; the failed attempt is immutable history; and
Slack delivery is at-least-once, so every click path must converge on one
attempt.

Stakeholders: Slack users who need a recovery path; the Server Slack control
and presentation services; the AgentSession / launch-coordinator owners; and
the TypeScript and Go adapter maintainers (contract coverage only).

## Goals / Non-Goals

**Goals:**

- Render a readable, sanitized, Server-owned failure notice for every failed
  Connection-originated attempt (initial launch and threaded follow-up),
  replacing reaction-only closeout as the terminal explicit-failure projection.
- Attach a signed, expiring, actor- and context-bound Retry control only for
  authoritative retryable failure categories, decided from category alone.
- Verify and re-authorize every Retry click at the existing interaction
  boundary with explicit user-visible outcomes and no execution side effect on
  rejection.
- Start exactly one fresh attempt from the original Slack request context:
  root failures re-launch with fresh identities under a retry idempotency key;
  threaded failures admit a force-new-turn follow-up in the original session.
- Commit a durable retry-operation record before dispatch and converge on one
  attempt across Slack redelivery, concurrent clicks, adapter failover, replay,
  and Server crash between commit and dispatch, with a fixed-key recovery
  reminder resuming committed-but-pending operations.
- Validate authoritative terminal state before dispatch and report accepted /
  already-applied / stale / unavailable explicitly.
- Extend terminal delivery events additively with session/input/turn identity
  and failure category; legacy events render without Retry.

**Non-Goals:**

- No new adapter grammar, endpoint, authorization logic, or persisted adapter
  state; Retry is pass-through on both the TypeScript adapter and the Go port.
- No change to Stop behavior, Agent reply ownership, reply-action promotion
  and idempotency, or Workflow recovery semantics.
- No retry control for input, configuration, permission, category-less,
  unknown, or legacy failures; retryability is never inferred from text.
- No mutation, reopening, or rewriting of the failed attempt's session, input,
  or turn records.
- No new third-party dependency and no new operator configuration surface.
- No approval-gate routing, slash commands, or non-Slack retry UI changes.

## Decisions

### A. The failure notice is its own explicit-failure outbox intent, keyed per failed attempt

`SlackTerminalDeliveryHandler` extends beyond `FinalizeLivenessAsync`: when a
terminal event reports status `failed` for a Slack-origin delivery, it also
renders one failure notice through a new `SlackStatusProjection` entry point
(`EnqueueExplicitFailureAsync`) that posts a message of outbox kind
`ExplicitFailure` under the stable dispatch key
`slack-failure:{sessionId}:{turnId}` (for legacy events without identity:
`slack-failure:{jobKey}`), in the originating conversation/thread, targeting
the attempt's source message for anchor purposes. Reactions keep closing
exactly as today; the notice is additive.

The notice presents only sanitized facts — a short reason summary, the failure
category when known, and one next-step sentence — produced through the same
redaction pipeline the terminal envelope already uses
(`AgentJobLineage.SafeSummaryFact`-style secret/token scrubbing, bounded
length). It must never reproduce raw provider output, credentials, endpoints,
or stack traces.

The notice deliberately does **not** promote the replaceable progress row in
place. The Agent reply action owns that promotion (the Agent's own failure
explanation usually claims it); the notice uses its own dispatch key so the
two deliveries never contend and the notice never embeds or overwrites
Agent-authored text.

Manager (Mohist App) deliveries render a text-only notice under the same rule
— liveness honesty already promises a terminal projection for every accepted
input — but get no Retry control (see D).

**Alternative considered: promote the progress message in place like the
reply action.** Rejected: it collides with the reply action's promotion right
and would make Server-owned failure text compete with Agent-authored text on
one message.

**Alternative considered: keep reaction-only closeout and route recovery
through the Web UI.** Rejected: no in-conversation recovery path, the core gap
the proposal names.

### B. Retryability is a static, code-owned category matrix

A small static classifier (`SlackFailureRetryPolicy`, Server-side, living
beside the failure-category vocabulary in `EventCatalog`) maps the
authoritative failure category to retryable/not-retryable. Retryable set:
`runner-unavailable`, `runner-lost`, `report-timeout`, timeout/deadline,
transport, and rate-limit categories (aligned with
`AgentJobFailureReasons` and the report-timeout reconciliation vocabulary).
Not retryable: category-less, unknown, legacy, input/configuration/permission
failures, context exhaustion, and everything not in the matrix.

The decision input is the terminal delivery event's `failureCategory` (and,
for follow-up turns, the turn result's category carried into the event — see
K). Failure text is never parsed or matched; a message that merely says
"timeout" changes nothing.

The matrix also derives the notice's next-step copy per class (retryable →
Retry available; input/config/permission → fix-then-resent guidance;
unknown/legacy → inspect the session timeline).

**Alternative considered: a configuration table so operators can tune the
matrix.** Rejected: no product requirement, adds an ops surface and drift
between Server instances for a security-relevant decision (YAGNI).

### C. One signed-action grammar shared with Stop; Retry binds the failed attempt

The Retry control is action id `mohist_retry_turn` with a `v1` payload record
analogous to `SlackStopActionPayload`: version, action discriminator,
connection id, the failed attempt's session/input/turn identity, the failure
notice's dispatch ref, actor Slack user id, initiator Slack user id,
conversation id, message ts, thread ts, nonce, and expiry. Signing reuses the
Stop machinery — extract the canonical-form + HMAC-SHA256 +
`FixedTimeEquals` helpers from `SlackTurnControlService` so both actions share
one implementation — with the key still being the Connection's BotToken
secret (`SecretKind.BotToken` at the Connection address). A Connection whose
signing credential cannot be loaded gets a text-only notice: no key, no
control.

Lifetime is bounded but longer than Stop's 5 minutes: Stop guards a live turn;
Retry must remain clickable after a user returns to a failed thread. Proposed
constant: 24 hours (see Open Questions).

**Alternative considered: a dedicated signing key per capability.** Rejected:
same installation trust domain, one credential address, one rotation story;
per-capability keys add surface without adding security here.

**Alternative considered: key the action on the notice message id instead of
the failed attempt identity.** Rejected: the message id is a presentation
fact; authorization must bind to durable execution identity.

### D. Clicks enter the existing boundary and re-authorize through the current Connection access policy

`SlackInteractionRoutes` keeps the single `/interactions` endpoint and
dispatches on action id: `mohist_stop_turn` unchanged; `mohist_retry_turn`
routed to the Retry branch of `SlackTurnControlService`, which verifies in
order:

1. parse + constant-time signature + version/discriminator;
2. expiry (bounded lifetime);
3. context binding: payload connection/team/conversation/message still match
   the live Connection and the click envelope;
4. actor: the click's actor equals the payload-bound actor;
5. nonce consumption: accept into the provider inbox under message identity
   `action:{nonce}` with a new `Retry` route kind — a consumed nonce is a
   replay that re-enters the recorded operation (H) rather than a second
   evaluation;
6. current authorization: evaluate the clicking actor through
   `SlackConnectionAccessDecider.EvaluateAsync` with the validated lease
   context — the same decision admitting an ordinary message. A payload-bound
   actor alone is not authorization; an owner who lost access, or an allowlist
   change, rejects here.

Every invalid, tampered, expired, stale, replayed, or unauthorized click
produces an explicit outcome message (UserAction `chat.update` on the notice
message) that removes or replaces the Retry control, with no execution side
effect. Manager sessions never get a Retry control: their provenance
addresses an Enrollment, not a Connection, so no Connection-bound signing key
exists — they satisfy "no key means no control" structurally.

**Alternative considered: authorize once at signing time (bind-then-trust).**
Rejected: Stop already re-validates per click; access policy can change
between notice render and click.

**Alternative considered: a new `/retry` endpoint.** Rejected: new adapter
grammar and attack surface; the existing boundary already provides lease
validation, dedup, and durable presentation.

### E. Terminal-state validation precedes any side effect; four explicit results

Before committing anything, a new target resolution (sessions querier + grain
reads) classifies the click into exactly one result:

- **accepted** — the target resolves, the failed turn is still `failed`, and
  no retry operation exists for it;
- **already_applied** — a retry operation exists for this failed target;
- **stale** — the attempt is no longer failed (settled, recovered, cancelled,
  or unknown — only `failed` qualifies);
- **unavailable** — session/input/turn cannot be resolved from durable state.

Root vs. threaded kind is derived from the failed turn's own records: a turn
carrying the launch Job binding (`JobId` set / session's initial launch) is a
root request; a `JobId`-less turn is a threaded follow-up. Anything that fails
validation reports its result explicitly and dispatches nothing.

### F. Root retry = retry-keyed re-launch through the shared launch coordinator

`LaunchConnectionAsync` gains an explicit idempotency-key override (default:
today's `slack:{team}:{conversation}:{messageTs}`). The retry passes
`slack-retry:{connectionId}:{failedSessionId}:{failedTurnId}` and pre-mints
session/input/turn ids deterministically from that key (same
`AgentLaunchCoordinatorCodec.StableToken` scheme as `PreMintSlackLaunchIds`),
so replays resolve to the same identities and a conflicting payload under the
same key is rejected as an idempotency conflict by the existing coordinator
fencing. The prompt is the failed input's text; the `ConnectionLaunchOrigin`
is copied from the failed input's provenance (same team, conversation,
message, thread root); accepted attachment descriptors from the original input
are re-bound onto the new input (availability re-checked; see Open Questions).
No Slack-only dispatch shortcut exists — the retry calls the same
`IAgentLauncher` boundary a CLI or Web surface would.

Because a root retry creates a **new** session, the Server rebinds its own
routing state — the thread session mapping (channel) or current-session
mapping (DM) — to the fresh session. The mapping is conversation routing
state, not the failed attempt's history; the failed session's records stay
immutable and readable.

**Alternative considered: reopen the failed session's initial turn.** Rejected:
mutates immutable history and violates the spec's fresh-identity requirement.

**Alternative considered: synthesize a new message identity to reuse the
existing default idempotency key.** Rejected: the retry key must be derived
from the retry operation, not from provider message facts the user never sent.

### G. Threaded retry = force-new-turn follow-up admission in the original session

`AcceptFollowupCommand` gains a force-new-turn admission mode. In the domain,
that mode bypasses `ChooseFollowupTurnForAssignment` entirely: it always
creates its own new Turn (queued when another turn is executing or queued) and
never attaches the input to an existing turn. Ordinary messages keep today's
steer/new-turn behavior unchanged, and an unrelated queued or executing
follow-up continues its own admission untouched.

The retry follow-up carries the retry idempotency key, pre-minted input/turn
ids derived from it, and provenance copied from the failed input (identical
member/message/thread facts, so reply anchoring and thread binding are
unchanged; the clicking actor is recorded on the retry operation, not forged
into provenance). Dispatch is operation-targeted: the session grain exposes a
turn-targeted variant of `BeginNextFollowupDispatchAsync` and
`AgentSessionFollowupDispatcher` dispatches that specific turn, so the retry
never depends on FIFO position and never rides an unrelated follow-up's
dispatch.

**Alternative considered: rely on the existing next-followup FIFO sweep.**
Rejected: the spec requires the retry to target its own turn; FIFO ordering
under concurrent ordinary follow-ups makes that nondeterministic.

### H. A durable retry-operation record commits before any dispatch

A new `SlackRetryOperationStore` (own EF migration; see Migration Plan)
persists one row per retry operation:

```text
SlackRetryOperations
  action_key          # unique; stable hash of the signed action identity (nonce)
  retry_dispatch_key  # slack-retry:{connection}:{failedSession}:{failedTurn}
  target              # root | followup, failed session/input/turn ids, connection id
  pre_minted_ids      # fresh session/input/turn (root) or input/turn (followup)
  state               # pending | dispatching | applied | failed
  outcome             # recorded result + presentation facts when settled
  recovery_lease      # optimistic claim (generation, deadline)
  timestamps
  UNIQUE(action_key), UNIQUE(retry_dispatch_key)
```

Commit precedes dispatch: the row is inserted `pending` before any execution
side effect; a commit failure aborts the retry with an explicit outcome and no
side effect. Concurrency converges on the unique keys — a concurrent click,
Slack redelivery, or adapter failover hitting the same action key re-enters
the existing operation and returns its recorded outcome (re-projecting the
presentation if needed, not a bare duplicate receipt). The
`UNIQUE(retry_dispatch_key)` makes "already applied" structural: one retry per
failed attempt even if a second action value were ever minted for it.

**Alternative considered: encode the operation inside the provider inbox
route.** Rejected: the inbox dedups provider events, but a retry operation
carries pre-minted identities and outcome state that outlive inbox dispatch
marking; a dedicated store keeps the inbox a pure ingress dedup.

### I. Crash recovery through a fixed-key reminder grain

A `SlackRetryRecoveryGrain` mirrors `SlackOutboxDispatcherGrain`: fixed string
key (rogue activations ignored), persistent reminder, startup activation
service, and a post-commit poke from the click path. The reminder scans for
committed-but-pending rows whose recovery lease expired and resumes them:
re-drive the launch coordinator (root) or the turn-targeted follow-up dispatch
(threaded) using the pre-minted identities, then complete the mapping rebind
if the crash interrupted it. The execution layer's existing idempotency keys
are the true exactly-once fence; the recovery lease is only an optimistic
claim preventing two in-flight drivers from duplicating presentation work.

**Alternative considered: per-operation reminder grains keyed by action.**
Rejected: unbounded reminder churn for a bounded table; the fixed-key sweep is
the established pattern for exactly this recovery shape.

### J. Every Retry outcome produces a durable presentation update

- **Accepted:** the failure notice is updated in place (UserAction
  `chat.update` at the notice message identity) to acknowledge the retry and
  project the new attempt's working state, including a newly signed Stop
  control built with the existing `CreateStopActionAsync` for the fresh
  turn — the same projection helpers the ingress paths use after a launch or
  follow-up. The new attempt's liveness then flows through the ordinary
  terminal pipeline; if it fails again, that pipeline renders a new failure
  notice with a fresh Retry action (a new attempt identity, so the unique-key
  rule is not violated).
- **Rejected / stale / unavailable / already applied / expired / replayed:**
  the obsolete Retry control is removed or replaced on the notice with the
  explicit outcome text, delivered through the existing outbox durability.

Outcome updates key on the existing `ActionDispatchRef` hash of the action
value, so repeated delivery of the same click converges on one presentation
mutation. Clicks are audited (workspace, conversation, actor, target,
outcome) like every other Slack control operation.

### K. Terminal delivery contracts extend additively; legacy events render without Retry

`SlackTerminalDelivery` gains optional `sessionId`, `inputId`, and `turnId`:

- Initial-launch events (`AgentJobLineage.BuildTerminalDeliveryEnvelope`)
  stamp them from `AgentJobInput.AgentSessionId / InitialInputId /
  InitialTurnId`; they already carry `failureCategory`.
- Follow-up events (`AgentSessionGrain.TryEmitFollowupDeliveryAsync`) stamp
  the turn's ids and — today hardcoded null — carry
  `turn.Result.FailureReason` / `FailureCategory`.

Events without identity or category facts (legacy, or any producer that cannot
resolve them) still render a readable notice from the available facts and
expose no Retry control; nullable fields plus JSON-tolerant deserialization
keep old events and old readers compatible in both directions.

### L. Adapters remain pure pass-through; contract coverage only

`normalizeSlackInteraction` (TypeScript) and the Go `InteractionEnvelope`
already forward arbitrary `actionId`/`actionValue` under the lease identity,
and both adapters deliver Server-provided blocks unchanged. The change adds
contract tests on both sides — `mohist_retry_turn` forwarding, prompt
acknowledgement, and blocks pass-through for the notice and its outcome
updates — and no adapter production code.

## Risks / Trade-offs

- [BotToken rotation invalidates signatures of outstanding Retry actions] ->
  bounded action lifetime caps exposure; affected clicks get an explicit
  invalid/expired outcome and the notice text stays readable; rotation is a
  rare, deliberate self-hosted operation with the same effect on Stop today.
- [Server-owned failure text could duplicate or shadow the Agent's own
  explanation] -> the notice uses its own dispatch key, never promotes the
  reply row, and renders only sanitized reason/category/next-step facts.
- [Thread/DM mapping rebind races a concurrent ordinary follow-up] -> the
  rebind happens inside the retry operation's commit-and-drive sequence and is
  idempotent; a racing follow-up lands either in the old session (still valid,
  readable history) or the fresh one after rebind — never lost.
- [Crash between operation commit and dispatch or mapping rebind] -> the
  fixed-key recovery reminder resumes from the record; execution fences
  (coordinator key / followup key) make resumption exactly-once.
- [Retry storms: repeated transient failures re-notified and re-clicked] ->
  one retry operation per failed attempt (unique target key); each retry is a
  new attempt identity with its own notice; the bounded action lifetime limits
  the clickable window.
- [Unique-constraint race on concurrent first clicks] -> single-row insert
  decides the winner; losers re-read and return the winner's recorded outcome.
- [Long notice lifetime grows stale turn references in signed payloads] ->
  expiry plus terminal-state validation make stale clicks explicit `stale`
  outcomes with no side effect.
- [Manager sessions get text-only notices (no Retry), creating an asymmetric
  recovery story] -> accepted for v1; Manager operators have CLI/Web; flagged
  in Open Questions.
- [Sanitization gaps leak internals into notices] -> reuse of the proven
  summary-fact redaction pipeline plus tests over raw provider error shapes.

## Migration Plan

1. **Schema.** One additive EF Core migration creating `SlackRetryOperations`
   (attributes per db-migrations.md authoring rules). Applied at startup by
   `DatabaseInitializer`; no data backfill — rows are created only by new
   clicks.
2. **Events.** New terminal-delivery fields are additive JSON properties;
   consumers treat missing fields as legacy. Failures that occurred before
   deploy simply render without Retry (their events lack identity).
3. **Deploy order.** Server-only; adapters need no coordinated deploy. New
   failure notices start appearing for failures emitted after deploy.
4. **Rollback.** Redeploy the previous Server build: the retry table stays
   inert, older handler code ignores the new JSON fields
   (`System.Text.Json` skips unknowns), and closeout reverts to
   reaction-only. Committed-but-pending retry operations after a rollback are
   abandoned rows with no in-flight side effects — the launch coordinator or
   follow-up idempotency keys were the fences; a later redeploy's recovery
   sweep settles them.
5. **Verification.** Contract tests on both adapters; server tests for
   notice rendering (retryable, non-retryable, legacy, Manager), click
   verification matrix, force-new-turn admission, commit-before-dispatch,
   concurrent/redelivery/crash convergence, and terminal-state results.

## Open Questions

- Exact Retry action lifetime (proposed 24h constant, mirroring Stop's
  code-owned constant) — confirm at implementation.
- Attachment re-binding: re-verify availability at click time (proposed) and
  how an attachment that became unusable is reported in the accepted-retry
  acknowledgement.
- Whether Manager (Mohist App) failure notices should later carry a control
  once an Enrollment-addressed signing credential exists — explicitly out of
  scope for v1.
- Exact next-step copy per failure class (retryable vs. fix-then-resent vs.
  inspect-timeline) — settled during implementation with the product doc.
- Whether `unknown` terminal outcomes deserve their own "uncertain" notice
  (e.g., linking the Web timeline) — currently out of scope; `unknown` keeps
  the attention reaction only.
