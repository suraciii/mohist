# Design

## Context

Slack fans one channel event out to every mentioned App, so a message that
mentions several Mohist Bots at a channel root or inside an existing thread
(or an unmentioned reply in a thread bound to several Connections) reaches
every mentioned Connection's ingress concurrently.
Today the race winner of `SlackAmbiguousPromptStore.TryClaimAsync`
(`SlackConnectionRoutes.ChannelIngress.cs`, `HandleAmbiguousPromptAsync`)
answers with a plain-text prompt — "Multiple Agents could answer this; mention
a single Bot to address one" — delivered through the winner's outbox under the
stable dispatch reference `slack-ambiguous:{team}:{conv}:{messageTs}`. The user
must retype the delegation, and the re-sent message becomes the execution's
provenance instead of the original.

The building blocks this change joins together already exist and are reviewed:

- **Signed interactions.** `SlackTurnControlService` (Stop) and
  `SlackRetryActionService` (Retry) sign canonical payloads with the
  Connection's bot token via `ISlackActionSigner` (HMAC-SHA256, hex,
  `CryptographicOperations.FixedTimeEquals`), bind actor/context/expiry, and
  are dispatched from `SlackInteractionRoutes` behind adapter operator
  authentication and runtime-lease validation.
- **Outbox blocks.** `SlackDeliveryPayload.Blocks` flows through outbox
  deliveries; the Go adapter's delivery path posts/updates blocks generically.
- **Generic adapter interactions.** `NormalizeSlackInteraction` forwards any
  `block_actions` action id/value; `actions[0].value` is populated for button
  clicks.
- **Idempotent launch.** The channel-root launch path
  (`LaunchChannelRootAsync`) is already restart-safe via thread launch
  reservations, the provider inbox route, and pre-minted deterministic launch
  ids (`PreMintSlackLaunchIds`); the follow-up path (`RouteFollowupAsync`) is
  idempotent by message-identity key.
- **Obligation-worker pattern.** `SlackAgentAppBindingObligationWorker`
  demonstrates the background recovery shape: scan pending rows, retry
  idempotently, log failures, bounded interval.

Stakeholders: the channel ingress state machine, the interaction route, the
launch/admission/follow-up services, the EF schema, the Go adapter (tests
only), and `docs/slack.md` / `design/slack.md` status notes. Constraints: one
Mohist Server per workspace set (no cross-Server coordination), external
at-least-once Slack transport, and the spec rule that the ambiguous message
itself creates no execution resources.

## Goals / Non-Goals

**Goals:**

- Replace the free-text ambiguity prompt with one interactive chooser per
  ambiguous message, claimed once across concurrent Connections, Slack
  redelivery, and adapter failover.
- Retain the original message's normalized input facts, its ambiguity kind,
  and every candidate's complete owning-Project/Connection reference durably
  with the claim so an accepted selection starts work with no resend,
  including when prompt owner and selected Connection belong to different
  Projects in the same workspace, with the original sender as initiator of
  record and the original message identity/thread anchor as provenance.
- Bind every choice to a Server-signed payload verified with the same signing
  material, canonicalization, and constant-time comparison Stop and Retry use.
- Revalidate at click time: signature, freshness, context (including the
  chooser message identity), actor binding, the clicker's current permission
  under both the **prompt-owner** and **chosen** Connections' current access
  policies using each Connection's own current lease, and the chosen
  candidate's current executability — each failure a distinct visible notice
  creating no selection mutation or execution resources.
- Guarantee single execution attribution: one durably recorded selection per
  ambiguous message, pre-allocated execution identity, at-most-one
  AgentJob/Session/SessionInput under concurrent clicks, redelivery, failover,
  and lost responses; restart recovery; bounded cleanup of finished records.
- Route the selection action id through the existing interaction route with
  zero adapter contract change (tests only), reusing the launch/admission
  services the CLI and Web use.

**Non-Goals:**

- Coordination across separate Mohist Servers (still none).
- Managed-Bot self-message suppression (#616).
- Changing ordinary non-ambiguous single-Bot thread routing.
- Letting anyone other than the original sender click (the Owner cannot click
  another member's chooser; the spec binds the actor to the original sender).
- A second command grammar — the button is a shortcut to the same services.
- Making the chooser a dropdown (`static_select`); see Decision 2.
- Any interactive presentation — pagination included — of more than five
  candidates; the >5 case is the readable text fallback (issue non-goal).

## Decisions

### 1. One durable record: the ambiguity claim becomes the selection authority

Extend `SlackAmbiguousPromptRow` (and `SlackAmbiguousPromptStore`) rather than
introducing a second selection-operations table. The row keyed by
`(WorkspaceTeamId, ConversationId, MessageTs)` — already the once-only claim
key — gains:

- **Retained input facts** (written by the same `INSERT ... ON CONFLICT DO
  NOTHING` that claims the row): `SenderSlackUserId`, `TaskText` (all
  mentioned workspace-Bot mention tokens removed, not just the winner's own —
  a multi-mention variant of `RemoveBotMention`), `FilesJson` (the serialized
  `SlackIngressFile` list), plus the existing `ThreadTs` anchor, a non-null
  `AmbiguityKind` (`RootMultiMention`, `ThreadMultiMention`, or
  `MultiBoundThreadReply`), and an ordered `CandidateReferencesJson` snapshot
  of `(ProjectId, ConnectionId)` pairs.
  `WorkspaceBoundBot` already supplies both values from the workspace-wide
  lookup; preserving both prevents the prompt-owner's Project from being
  substituted when another Project owns the selected Connection. Facts and
  candidate references are non-nullable columns, so a claim cannot exist
  without them: the spec's "claim lacks facts cannot silently degrade" is
  enforced structurally, and acceptance never reconstructs input or performs
  a global Connection-id-to-Project guess.
- **Selection state**: `SelectionState` (`Pending` → `Decided` →
  `Completed` | `Settled`), `ChosenProjectId`, `ChosenConnectionId`,
  `DispatchKind` (`RootLaunch`, `ThreadLaunch`, or `ThreadFollowup`),
  `DecidedAt`, pre-allocated `SelectionSessionId`/`SelectionInputId`/
  `SelectionTurnId`, worker bookkeeping (`AttemptCount`, `LastAttemptAt`),
  and `FinishedAt`/`SettleReason` for retention. `ThreadFollowup` stores the
  selected binding's existing Session id; launch kinds store the pre-minted
  Session id.

The claim key *is* the ambiguous-message identity both the chooser fence and
the execution fence need. Two tables would force a two-phase coordination
(claim then operation) with its own partial-failure window, and the spec's
"the persisted selection record SHALL be the single authority" would be split
across rows. Alternative rejected: a separate `SlackSelectionOperationRow`
joined by claim id — cleaner column hygiene, but it re-opens exactly the
crash window (claim written, operation not) the single record closes.

The non-owner path (`HandleAmbiguousNonOwnerAsync`) is unchanged: same claim,
same once-only owner-only guidance text, no chooser, no facts consumed.

### 2. The chooser is one `actions` block of at most five buttons sharing one action id

Render one button per candidate Bot (label from the Bot identity, as the
current prompt summary does), all carrying action id
`mohist_select_agent`, each with its own signed value. Post it through the
winner's outbox `UserAction` delivery under the existing stable dispatch
reference, with readable text summarizing the candidates.

- **Interactive bound of five (issue AC #1 / Product Shape: 最多展示五个
  候选).** With two to five eligible candidates the chooser renders one
  button per candidate plus readable text. With more than five eligible
  candidates the winner renders **no interactive control at all**: the
  once-only delivery becomes a readable text fallback requiring the sender
  to explicitly re-mention a single Bot — no truncation (a partial button
  list would silently drop candidates the user could have chosen), no
  auto-selection, and no pagination (an explicit non-goal of the issue).
  The claim and its once-only semantics are unchanged; only the rendering
  differs, so fan-out, redelivery, and failover still produce exactly one
  fallback message.
- **Readable text in every case.** The chooser message's plain-text field
  always carries the candidate summary and the single-Bot re-mention
  instruction, so a client that cannot render the interactive controls
  (the Product Shape's "Slack interaction 不可用" leg) shows the same
  guidance.
- **Buttons, not a `static_select`**: the Go adapter's
  `NormalizeSlackInteraction` reads `actions[0].value`, which Slack populates
  for button clicks; a select menu puts the value in
  `selected_option.value`, which the adapter does not read — that would be an
  adapter contract change the spec forbids. With the five-candidate cap the
  actions-block element limit is never the operative constraint; the cap is
  the product rule, not a UI bound.
- **Candidate set and ambiguity kind**: explicit multi-Bot mentions use
  `MentionedWorkspaceBots` (parsed mentions ∩ workspace identity-bound Bots,
  deduplicated by Bot user id) at both a channel root and inside an existing
  thread; humans and other-Servers' Bots never appear, and duplicate Bot
  identities do not add ambiguity. An unmentioned ambiguous thread reply uses
  the workspace-wide binding query. The claim records which of these three
  ingress shapes produced the chooser. Both workspace queries remain
  deliberately not Project-filtered: extend `SlackThreadBinding` to project
  `ProjectId` from its mapping row, and carry `(ProjectId, ConnectionId)`
  through `SlackMultiAgentRoutingCandidate` and
  `SlackMultiAgentRoutingDecision` rather than returning bare Connection ids.
  Mention choices retain `WorkspaceBoundBot.ProjectId`; binding choices retain
  `SlackThreadBinding.ProjectId`; label joins use the complete pair. The
  routing policy's attribution behavior is unchanged; only candidate identity
  and the durable ambiguity kind become complete, and the `Prompt`
  disposition's side effect changes from text to blocks (or, beyond five
  candidates, to the text fallback).
- **Placement**: the delivery keeps the inbound `ThreadTs`, so a root chooser
  posts at the channel root and a thread chooser posts in that thread, exactly
  as the current prompt does.

### 3. The signed payload binds the original message identity; the chooser message identity is enforced durably

`SlackSelectionActionPayload` (beside `SlackStopActionPayload`): `Version`
("v1"), `Action` ("select_agent"), posting `ProjectId` and `ConnectionId`,
`WorkspaceTeamId`, `ConversationId`, `OriginalMessageTs`, `ThreadTs`,
`AmbiguityKind`, `ActorSlackUserId` (the original sender), the ordered candidate reference set
of `(ProjectId, ConnectionId)` pairs, `ChosenProjectId`,
`ChosenConnectionId`, `Nonce`, `ExpiresAt` — signed with `ISlackActionSigner`
over a `\n`-joined canonical form, identical in shape to Stop/Retry. The
canonical representation length-prefixes or otherwise unambiguously encodes
each pair and preserves ordering; Project and Connection ids are never signed
as independent unordered lists.

The chooser's own Slack message ts is assigned by Slack at delivery time and
cannot be known when the value is signed. The spec still requires the
interaction's chooser-message identity to match. Resolution: the payload binds
the **original** message identity (known at render), and acceptance enforces
the chooser-message binding server-side by resolving the chooser's outbox row
via the stable dispatch reference and comparing its persisted
`ProviderMessageIdentity.MessageTs` (written by `MarkDeliveredAsync` on ack)
against the interaction envelope's container `MessageTs`. A click on any other
message — a stale duplicate chooser created by an outbox delivery retry, or a
copied button value — is rejected as stale. When no provider identity has been
acked yet, the chooser is not confirmed and the click is rejected as stale
(signature, actor, and team/conversation still hold, so this fails closed with
a visible notice and no resources); the window is bounded by the expiry and
self-heals when the outbox retry acks a delivered chooser. Alternatives
rejected: signing at delivery time (the outbox renders payload content at
enqueue; a two-phase sign-then-update would be a new delivery mechanism) and
omitting the chooser-message check (reopens replay from duplicated chooser
messages).

Expiry is fixed when the chooser is rendered at the issue-pinned **five
minutes** — the same signed-action lifetime Stop and Retry use
(`动作沿用现有五分钟有效期`); no new action lifetime is introduced. A user
who returns to the thread later gets a visible `expired` notice and the
re-mention fallback — the same posture as the >5-candidate and
interaction-unavailable text fallbacks. Actor binding is the original
sender only; the Connection Owner clicking another member's chooser is
`unauthorized`, matching the spec's strict actor rule (Stop/Retry's
owner-or-initiator relaxation does not carry over).

### 4. Acceptance is a fixed-order revalidation pipeline, each failure distinct and visible

`SlackAgentSelectionService.HandleAsync` (beside the Stop/Retry services),
running after the shared route has done operator auth, lease validation,
envelope completeness, and delivering-Connection lookup/disabled check:

1. **Signature/structure** (`invalid_action`) — deserialize, field presence,
   constant-time HMAC verify. Tampered or foreign-key values die here.
2. **Freshness** (`expired`).
3. **Context** (`stale_action`) — envelope team/conversation match payload and
   delivering `(ProjectId, ConnectionId)`; the claim row resolves for the
   payload's original message identity and was posted by that same pair; the
   payload's ambiguity kind and ordered candidate references exactly match the
   durable snapshot; the chooser-message identity check of Decision 3.
4. **Actor binding** (`unauthorized`) — clicker equals the bound original
   sender.
5. **Recorded decision** — if the claim is already `Decided`/`Completed`, skip
   to the decision view (late clicker, redelivery, lost response). Placed
   before permission/executability so replays never re-evaluate mutable state.
6. **Prompt-owner permission** (`unauthorized` + actionable reason) — invoke
   `SlackConnectionAccessDecider.EvaluateAsync` for the posting Connection
   under its current policy, allowlist, owner/live-member, and channel-
   membership state. Build its `SlackLeaseContext` from the route-validated
   interaction `LeaseId`/`AdapterId` and operator id, with
   `ResolveRuntimeLeaseBotTokenAsync` bound to that same pair. The shared
   route's lease check proves that this is the prompt-owner Connection's
   current lease; this separate decider call proves that the clicker remains
   authorized under that Connection's current mutable access state. A denial
   stops before candidate handling or mutation.
7. **Candidate resolution and validity** (`unavailable` or
   `no_longer_valid`) — after step 3 has proved that the chosen
   `(ChosenProjectId, ChosenConnectionId)` pair is in the signed payload and
   exact durable candidate snapshot, resolve that pair with the normal
   project-scoped
   `AgentConnectionStore.GetAsync(ChosenProjectId, ChosenConnectionId)`, then
   explicitly require `selectedConnection is not null &&
   selectedConnection.DeletedAt is null`. The current `GetAsync` intentionally
   includes soft-deleted rows, while `AgentConnectionStore.DeleteAsync` marks
   deletion by setting `DeletedAt`; therefore either a null result or a
   non-null `DeletedAt` returns the distinct visible `unavailable` outcome
   required by issue AC #4. This deletion check happens before workspace-Bot
   binding validation, lease resolution, authorization, executability, or
   commit, so surviving/stale lease or binding artifacts cannot make a
   soft-deleted candidate executable. If the active Connection still exists,
   prove it remains bound to the chooser's workspace Bot; drift from those
   snapshotted candidate facts is `no_longer_valid`. No global lookup by
   Connection id and no fallback to the prompt-owner Project is allowed. A
   vanished follow-up target session in `ChosenProjectId` is also
   `no_longer_valid` because the selected Connection remains active but the
   retained thread-dispatch facts no longer hold.
8. **Selected-Connection lease** (`unavailable`) — for the selected
   Connection that exists after step 7, resolve its **own current runtime
   lease** at click time: read the active lease for its target key
   `connection:{ChosenProjectId}:{ChosenConnectionId}`
   from the lease store (`ISlackLeaseStore.GetActiveAsync`), then re-prove
   it through `SlackAdapterLeaseService.ValidateRuntimeLeaseAsync` with the
   store-resolved `LeaseId`/`AdapterId` (which also fails closed when the
   target is no longer active/verified/bot-token-provisioned or the pinned
   credential generation rotated). Absent, expired, superseded, or otherwise
   invalid → distinct visible `unavailable` outcome, no resources (issue
   AC #4: lease 失效返回 unavailable). The lease is **never** derived from
   the interaction's delivering adapter: the interaction arrives over the
   posting (prompt-owner) Connection's socket, and leases are per-target
   (`connection:{ProjectId}:{ConnectionId}`), so both components must come
   from the selected candidate reference. Presenting the prompt-owner's
   `LeaseId`/`AdapterId` or Project id against the chosen Connection's
   target simply fails validation. Retry gets away with the interaction's
   lease only because its evaluated Connection *is* the delivering
   Connection; for a chooser, picking a candidate other than the posting
   Connection is the mainline case the feature exists for.
9. **Selected-Connection permission** (`unauthorized` + actionable reason) —
   invoke `SlackConnectionAccessDecider.EvaluateAsync` under the **chosen**
   Connection's current policy, with a `SlackLeaseContext` built from the
   chosen Connection's own lease resolved in step 8 (operator id plus the
   resolved `LeaseId`/`AdapterId`, with `ResolveRuntimeLeaseBotTokenAsync`
   bound to that pair) — **not** the interaction's delivering lease
   (issue AC #3: prompt-owner lease 不被复用). Render-time authorization is
   never trusted. When the posting and chosen Project/Connection pairs are
   equal, the prompt-owner evaluation in step 6 is an equivalent current
   evaluation under the same current lease and may satisfy this step without
   a duplicate Slack API call.
10. **Executability** — evaluate the chosen Agent and Connection in
   `ChosenProjectId`; chosen Connection disabled → existing
   `connection_disabled` outcome; Agent not ready → the existing
   `SlackAdmissionService` setup-nudge path, invoked with the original
   message identity so its once-only nudge deduplicates per ambiguous message
   (and per the admission store's existing dedup, survives redelivery).
11. **Commit** — the decision fence (Decision 5), then dispatch (Decision 6).

Steps 1–10 create no AgentJob, Session, SessionInput, selection record
mutation, or provider inbox entry. Outcome names map onto the issue's domain
model as follows: the issue's `unavailable` ← step 7's absent selected row or
soft-deleted selected row (`DeletedAt` is non-null), step 8's missing/invalid
selected-Connection lease (both
adopted verbatim from AC #4), and, for its broader "目标当前不可执行" leg,
the existing `connection_disabled` and setup-nudge outcomes; the issue's
`unauthorized` ← either current-policy denial in step 6 or 9, plus actor
mismatch; the issue's `stale` ← `expired`, `stale_action`, `invalid_action`,
and `no_longer_valid` for a still-existing candidate whose snapshotted
workspace/thread facts no longer hold. Every outcome returns
`SlackTurnControlResult`-shaped state/text/blocks; the route's existing reply
enqueue updates the chooser message via `chat.update`, so late and second
clickers see the decision instead of a second chooser. Reply idempotency comes
free: the route's `ActionDispatchRef` (SHA-256 of the action value) makes an
interaction redelivery coalesce onto the already-delivered update.

### 5. The decision fence is a compare-and-swap on the claim row; no extra inbox row

Before the conditional update, dispatch classification uses only retained
provenance plus the selected candidate's current thread binding:

- `RootMultiMention` always commits `RootLaunch`, with the original message ts
  as the thread root.
- `ThreadMultiMention` commits `ThreadFollowup` when the selected
  Project/Connection is already bound under the retained `ThreadTs`; otherwise
  it commits `ThreadLaunch`, using that retained `ThreadTs` as the launch-and-
  bind anchor. It never substitutes the reply's own message ts as a new root.
- `MultiBoundThreadReply` requires the selected candidate's existing binding
  and commits `ThreadFollowup`; a missing binding is `no_longer_valid`.

The commit step is a single conditional update:
`UPDATE ... SET SelectionState='Decided', ChosenProjectId=...,
ChosenConnectionId=..., DispatchKind=...,
SelectionSessionId/InputId/TurnId=... WHERE Id=... AND
SelectionState='Pending'`, evaluated atomically in the store. Concurrent
clicks on the same or different candidates, repeated clicks, Slack
interaction redelivery, and adapter failover all collapse: one CAS wins, every
loser re-reads the row, observes the complete chosen pair, and returns the
same decision view. The execution identity is pre-allocated at CAS time according to the persisted
`DispatchKind`: `RootLaunch` and `ThreadLaunch` reuse
`PreMintSlackLaunchIds(ChosenProjectId, originalMessageIdentity)` so
Project-scoped deterministic ids are minted for the selected Project, not the
prompt owner's; `ThreadFollowup` records the selected candidate's existing
bound Session id plus deterministic input/turn identity. Replays use the
persisted kind and ids and do not reclassify the message.

Stop needs a provider-inbox `action:{nonce}` row because stopping has no row
fence of its own; here the claim row *is* the fence, and the accepted
selection enters the provider inbox through the launch/follow-up path it
triggers (`LaunchThread`/`FollowupThread` route kinds) — satisfying "an
accepted click enters the durable provider inbox like any other input" without
a redundant dedup row that rejections would then have to avoid creating.

### 6. Dispatch reuses the launch and follow-up services under the chosen Project and Connection

Every project-scoped call in dispatch receives the committed
`ChosenProjectId`; the interaction route's prompt-owner `ProjectId` is not an
execution default.

- **Root multi-mention / `RootLaunch`**: run the existing channel-root launch
  sequence for the chosen Connection in `ChosenProjectId` from the retained
  facts — thread launch reservation on the original message ts as thread root,
  provider inbox accept with the original message identity, attachment binder
  over `FilesJson`, `InteractionWorkspaceProvisioner`,
  `LaunchConnectionAsync` with `ConnectionLaunchOrigin` built from the
  **original** sender and message identity, and pre-minted ids from the
  decision record.
- **Explicit multi-Bot mention in an existing thread**: resolve the chosen
  candidate's binding under the retained original `ThreadTs` at click time.
  An existing binding executes `ThreadFollowup` through `RouteFollowupAsync`;
  an unbound choice executes `ThreadLaunch` through the same extracted launch
  service, using the retained `ThreadTs` as the thread anchor so the selected
  Bot gains an independent Session in the existing thread. The reply's own
  message ts never becomes a replacement thread root.
- **Unmentioned multi-bound-thread reply / `ThreadFollowup`**: dispatch a
  follow-up to the chosen Connection's bound Session in `ChosenProjectId` via
  `RouteFollowupAsync` with the retained reply facts and message-identity
  idempotency key; a missing binding is `no_longer_valid` and never launches a
  new Session.

Because `LaunchChannelRootAsync` is private to the route partial and bound to
the delivering Connection's request, extract its core into an internal service
(e.g. `SlackChannelLaunchService`) parameterized by project id, connection,
identity, sender, text, files, and thread anchor. Both root launch and existing-
thread launch-and-bind use this service, so selection remains the same launch
path as ordinary channel ingress.
- Loser Connections stay no-ops: the multi-mention and multi-binding ingress
  branches always route through the claim, which exists from render time on,
  so no mentioned Connection can create work for the message by any path.

### 7. Recovery and cleanup follow the obligation-worker pattern

A `SlackAgentSelectionObligationWorker` (mirroring
`SlackAgentAppBindingObligationWorker`) periodically:

- **Resumes** `Decided` rows whose dispatch has not completed. Re-running the
  dispatch is safe: the launch path is idempotent (reservation, inbox route,
  deterministic pre-allocated ids) and the follow-up path is idempotent by
  message identity. A restart between commit and dispatch therefore resumes to
  the same execution identity, never a second one. Recovery re-runs the
  dispatch only — per the issue's winner-commit rule it never re-authorizes,
  never depends on the original click's lease, and never changes the chosen
  candidate or its owning Project (the pipeline's mutable-state checks,
  including prompt-owner permission in step 6 and selected-Connection
  lease/permission in steps 8–9, live before the commit fence and are skipped
  by replays). Recovery resolves, dispatches, admits, and settles using the committed
  `ChosenProjectId`, `ChosenConnectionId`, `DispatchKind`, retained thread
  anchor, and pre-allocated identity, never the row's prompt-owner `ProjectId`
  as the selected Project and never reclassifying launch versus follow-up from
  mutable bindings.
- **Settles terminally** when a committed selection can no longer produce its
  execution (chosen Connection or Agent deleted after repeated failures, or
  the pre-allocated lineage irrecoverable): mark `Settled` with a reason and
  post one visible outcome through the outbox `UserAction` path under a
  stable dispatch reference; nothing may execute afterwards.
- **Expires stale pending choosers**: a `Pending` row whose five-minute
  expiry has passed is settled `expired` (no additional grace — the
  freshness check rejects a late click as `expired` regardless, so sweep
  timing is not correctness-critical; the sweep interval only needs to stay
  short relative to the retention window, which the obligation-worker
  pattern's bounded intervals already provide). This is what makes retention
  safe — the spec forbids reaping pending/in-progress *operations*, and an
  expired chooser is no longer an operable one.
- **Reaps** finished records: `Completed`/`Settled` rows older than the
  **existing** Slack event retention window
  (`SlackProviderOptions.SlackEventRetentionWindow`, 30-minute default) — the
  redelivery / delivery-reconciliation posture the issue pins (`只保留到现有
  Slack redelivery 与 delivery-reconciliation retention window，不新增长期
  审计存档`) — are deleted. No new, materially longer retention regime and no
  long-term audit archive are introduced; pending/in-progress rows are never
  removed. A late interaction redelivery is visible and resource-free either
  way: past the five-minute expiry the freshness check rejects it as
  `expired` even while the record lives, and after the window reaps the
  record it returns `stale_action`.

### 8. Route integration is a three-way action-id dispatch; the adapter changes only in tests

`SlackInteractionRoutes` dispatches by action id: Retry (as today, with lease
context), the new selection id (with a `SlackLeaseContext` built from the
route-validated delivering adapter lease for the prompt-owner authorization
in Decision 4 step 6), and Stop/unsupported via the existing fall-through.
The selection service resolves the chosen Connection by the signed and
snapshotted `ChosenProjectId`/`ChosenConnectionId` pair and resolves that
Connection's own current lease internally per Decision 4 step 8; it must not
evaluate that Connection under the delivering lease or prompt-owner Project.
Adapter operator authentication, runtime-lease
validation (stale lease → existing `lease_stale_or_expired` before any
selection processing), delivering-Connection lookup, disabled check, and the
reply enqueue are reused unchanged. The route lease gate and service-level
prompt-owner access decision are intentionally distinct: the former proves
adapter authority, while the latter re-evaluates current policy, allowlist,
live-member, and channel-membership state. The Go adapter needs no code change:
`block_actions` forwarding is generic and blocks already flow in deliveries;
adapter tests add the selection action id to their coverage. `docs/slack.md`
and `design/slack.md` move the multi-Bot selection row from planned to
delivered.

## Risks / Trade-offs

- [Outbox retry double-posts the chooser; the duplicate's buttons must not
  resolve] -> Strict chooser-message-identity check against the acked provider
  identity (Decision 3): only one live chooser message, duplicates' clicks are
  visibly `stale_action` with no resources.
- [Click on a delivered-but-unacked chooser is rejected as stale] -> Fails
  closed with a visible notice; the window is bounded by expiry and self-heals
  on the retry's ack; the user may also re-mention a single Bot (today's
  fallback remains valid). Accepted trade-off for not weakening the context
  check.
- [Signing key rotated between render and click] -> Verification fails as
  `invalid_action`, same failure mode Stop/Retry already accept; visible, no
  resources; user re-mentions.
- [Long-lived pending rows accumulate if users never click] -> Bounded by the
  expiry sweep settling them (Decision 7); rows are small and one per
  ambiguous message.
- [A workspace-wide candidate belongs to a different Project from the prompt
  owner] -> Candidate identity is always the ordered `(ProjectId,
  ConnectionId)` pair from `WorkspaceBoundBot`; the snapshot, signed payload,
  CAS winner, selected lease target, authorization, launch/follow-up,
  pre-allocation, and recovery all preserve that pair. No component infers
  Project ownership from the interaction route.
- [Either Connection's permission or the chosen candidate's executability
  changes between click and dispatch] -> The CAS commits only after separate
  prompt-owner and chosen-Connection current-policy reauthorization plus
  executability revalidation; post-commit changes are the same race every
  launch path has (admission is re-run by recovery only for not-yet-completed
  dispatches); no second execution is possible because the fence is the row.
- [Extracting the launch core or adding existing-thread launch-and-bind risks
  regressing ingress routing] -> The extraction is behavior-preserving and
  covered by existing root/thread ingress specs plus new selection scenarios
  for root launch, existing-thread bound follow-up, existing-thread unbound
  launch-and-bind under the original anchor, and unmentioned multi-bound
  follow-up.
- [More than five eligible candidates] -> No interactive control is rendered
  at all: the once-only delivery is the readable text fallback requiring an
  explicit single-Bot re-mention — no truncation, no auto-selection, no
  pagination (non-goal) — and the claim keeps it to exactly one fallback
  message.
- [Late interaction redelivery after retention reaped the record] -> Past
  the five-minute expiry the freshness check rejects it as `expired` whether
  or not the record survives; once the existing retention window reaps the
  record it returns `stale_action` — visible and resource-free either way,
  with no new retention regime.

## Migration Plan

1. Add the EF migration: new columns and indexes on `SlackAmbiguousPrompts`
   (`AmbiguityKind`, `CandidateReferencesJson`, `ChosenProjectId`,
   `DispatchKind`, selection state, worker scan on
   `(ProjectId, SelectionState, UpdatedAt)`),
   additive only. Existing rows predate fact retention; they carry no facts
   and can never start an execution (enforced structurally), and age out via
   the new cleanup once settled — no backfill.
2. Land in one Server release: store + migration, chooser rendering replacing
   the plain-text prompt, selection service + route dispatch, launch-core
   extraction, obligation worker, adapter test coverage, doc updates. The
   ambiguous-message flow has no partial-deployment mode worth splitting: old
   choosers (none exist yet) and new choosers never coexist on one message.
3. Rollout is a single-Server deployment per workspace set; no cross-Server
   coordination is introduced.
4. Rollback: revert the Server; the added columns and any recorded selections
   are inert without the new code (claims revert to advisory-only). Choosers
   rendered before rollback render buttons whose clicks 404 on the old route —
   acceptable within the rollback window, and the plain re-mention fallback
   keeps working.

## Open Questions

- Button label source: Bot display name versus `@mention` label — the current
  prompt summary uses the Bot user id label; a friendlier verified Bot name
  may be preferable if cheaply available at claim time.
- Whether the decision view should name the initiator when a late clicker
  observes another member's accepted selection (privacy-neutral today: the
  chooser is actor-bound to one sender anyway).
