## Context

Issue 515 extends Slack Agent usage from one-to-one DMs into channels and threads, and is the
first change to handle multiple Agents coexisting in one thread. The product spec is
`docs/agent-connections.md:200-265`; the binding architecture is `design/slack-agent-connection.md:96-127`.
Prerequisites 514 (DM launch), 521 (follow-up semantics), 522 (cancel/stop), and 524 (DM
current-session + work control) are all done.

Current state (sourced):

- **Channel messages are discarded outright.** `SlackConnectionRoutes` ingress
  (`Api/SlackConnectionRoutes.cs:281`) returns `ignored` for every non-DM event
  (`if (body is null || !body.IsDirectMessage) return ignored`). There is no thread or mention
  handling on the channel path.
- **The ingress envelope carries no thread or mention identity.** `SlackIngressBody`
  (`Api/SlackConnectionRoutes.cs:973`) has only `IsDirectMessage/ConversationId/MessageTs/Sender`.
  The TS `SlackEnvelope` (`mohist-slack/src/types.ts:12`) and `normalizeSocketEvent`
  (`mohist-slack/src/adapter.ts:152`) extract neither `thread_ts` nor parsed mentions; the outbox
  drain posts with no `thread_ts` (`adapter.ts:127`).
- **Launch/follow-up machinery is reusable and already Slack-aware.** `LaunchConnectionAsync`
  (`Agent/Services/IAgentLauncher.cs:196`) routes through `AgentLaunchCoordinatorGrain` with an
  idempotency key `slack:{team}:{conversation}:{messageTs}` (`AgentLauncher.cs:203`). Follow-up
  goes straight to `IAgentSessionGrain.AcceptFollowupAsync` with the same key shape (524 D4,
  `SlackConnectionRoutes.cs:650`). Slack-launched sessions are stamped `source-kind=agent-connection`
  plus `connection-id/slack-user-id/slack-conversation-id` labels
  (`AgentLaunchCoordinatorGrain.cs:318-320`).
- **Conversation mapping exists only for DM current-session.** `SlackDmSessionMappingStore`
  (`Infrastructure/Slack/SlackDmSessionMappingStore.cs`) is keyed uniquely on
  `(ConnectionId, DmConversationId)` and swaps its `CurrentSessionId` on a New task (524 D1). It
  cannot represent one thread hosting several Agents, and thread binding has no New-task swap.
- **Provider infrastructure is established and per-Connection.** The inbox dedups by
  `(ConnectionId, SlackMessageIdentity)` (`SlackProviderInboxStore.cs:71-134`); the outbox is
  drained per-Connection (`SlackOutboxStore`, `adapter.ts:116`); both cascade on Connection delete
  via `IAgentConnectionProviderCleanup`. Terminal delivery is rendered by
  `SlackTerminalDeliveryHandler` (`:40-77`) and routed via `AgentJobLineage.BuildTerminalDeliveryEnvelope`
  (`:101-131`), both keyed on `DmConversationId` with no thread dimension.
- **No workspace-scoped bot resolution exists.** `AgentConnectionStore.ListForAdapterAsync`
  (`:69`) returns all configured Connections globally; there is no "which Bots live in this
  workspace" query, which multi-Agent attribution needs.
- **Channel access has no policy model — and does not need one for this issue.** `AgentConnection`
  (`Agent/Domain/AgentConnection.cs:21`) already holds `OwnerSlackUserId`; the issue scopes channel
  invocation to Owner only.

The repo is in active development with no version-compatibility constraint (`AGENTS.md`), so
internal renames and additive storage are acceptable.

## Goals / Non-Goals

**Goals:**

- Route a channel root mention to a launch that binds the Agent to the originating thread, and
  route a reply in a bound thread to a follow-up of that Agent's session — without re-mention.
- Let one thread bind multiple Agents, each with an independent session and context, and let a
  first mention of a new Agent bind it without disturbing the others.
- Refuse to guess the target when a message is ambiguous (multi-bot mention, or a multi-Agent
  thread reply naming no Agent), and prompt the user to choose at most once.
- Keep the acceptance gate narrow: only DMs, explicit mentions, and bound-thread replies enter
  Mohist; Bot-self messages, plain channel messages, and unidentified senders create nothing.
- Deliver acks and terminal results into the thread, and record workspace/channel/thread/member
  provenance on every accepted input.
- Preserve redelivery/restart idempotency on the channel path, reusing the existing launch and
  follow-up dedup.

**Non-Goals:**

- Access policies beyond Owner only (Allowlist/Anyone) and who may stop someone else's work —
  owned by the access-policy issue.
- Thread-history import and Slack file handling.
- Slack Connect external members, group DM, or cross-Mohist-Server multi-Bot coordination.
- Guessing the target Agent from natural language, channel topic, or the previous speaker.
- Channel-side cancel/stop keywords (DM work control is unchanged; channel control is not added).
- Web/CLI surfaces for channel sessions.

## Decisions

### D1. The adapter normalizes thread identity and the full mention list; the Server ingests a normalized envelope

`normalizeSocketEvent` (`mohist-slack/src/adapter.ts:152`) SHALL extract `thread_ts` from the
Slack event (absent on a root message; present on a thread reply) and parse every `<@U...>` token
from `event.text` into an ordered, de-duplicated list of mentioned Slack user ids. It also normalizes
sender facts as `human`, `bot`, or `unknown`: Slack Bot subtype / `bot_id` events are `bot`; an event
without a stable user id is `unknown`; all other events with a user id are `human`. Stable message
identity remains `(team, conversation, messageTs)`, so a Bot or unknown event is still acknowledged
even though it cannot become an input. `SlackEnvelope` (`mohist-slack/src/types.ts:12`) gains
`threadTs: string | null`, `mentionedUserIds: readonly string[]`, `senderKind`, and nullable
`senderSlackUserId`. The server-side `SlackIngressBody` (`Api/SlackConnectionRoutes.cs:973`) gains
the matching fields.

The mention list is the **complete** set parsed from the text, not a boolean "am I mentioned".
Cross-connection attribution (D4) needs to know whether *other* workspace Bots are also mentioned,
which requires the full list, not this connection's self-assessment.

**Rationale:** the adapter is the Slack wire-protocol boundary (`design/slack-agent-connection.md:46` —
it owns "Slack mention、thread、成员目录或平台限流" translation). Parsing `<@U...>` there keeps the
Server free of Slack tokenization detail, and the Server already trusts the adapter to produce a
stable identity (`team/conversation/ts`). Slack's `<@U...>` mention format is documented and stable.

**Alternatives:** (a) Forward raw `event.text` and parse in the Server — rejected: pushes Slack
wire-format knowledge into Server authority and across two trust seams. (b) Have the adapter report
only "my bot was mentioned" — rejected: cannot detect multi-bot mentions, which is the core
attribution requirement. (c) Continue rejecting events without `event.user` in the adapter —
rejected: the throw occurs before Socket Mode acknowledgement and turns an ignored Bot/unknown event
into an infinite redelivery.

### D2. A thread→session binding store keyed by (Connection, Workspace, Channel, ThreadTs), with durable reconciliation

Add `Infrastructure/Slack/SlackThreadSessionMappingStore.cs` with
`Infrastructure/Data/Slack/SlackThreadSessionMappingRow.cs`, uniquely indexed on
`(ConnectionId, WorkspaceTeamId, ConversationId, ThreadTs)` → `SessionId` (plus
`ProjectId/SlackUserId` denormalized for audit). `WorkspaceTeamId` and `ConversationId` are part of
the key because Slack's stable message identity is workspace + channel + timestamp; a Project can
manage Connections in several workspaces and the same Connection can be present in several channels.
It implements `IAgentConnectionProviderCleanup` so Connection delete cascades. Operations are:

- `GetSessionIdAsync(projectId, workspaceTeamId, connectionId, conversationId, threadTs)`;
- `ListBindingsAsync(projectId, workspaceTeamId, conversationId, threadTs)` to resolve the exact set
  and cardinality of Agents bound to one thread; and
- `BindAsync(projectId, workspaceTeamId, connectionId, conversationId, threadTs, sessionId)`, an
  idempotent upsert that does **not** swap on repeat (a bound `(connection, workspace, channel,
  thread)` keeps its session; threads have no New-task switch).

Thread identity is the root message ts: for a root mention it is the message's own ts; for a reply
it is the envelope `thread_ts`.

Multi-Agent-per-thread falls out of the key: different Connections produce different rows under the
same `(workspace, conversation, thread_ts)`, each pointing at its own session. There is no "current
session per thread" shared across Agents.

`LaunchConnectionAsync` and the mapping write cannot share a transaction. The launch branch SHALL
therefore first persist the resolved session id on the accepted root inbox route, then idempotently
bind that persisted id before replying. Every channel ingress that finds no mapping SHALL reconcile
before classifying the message: it looks up the accepted root inbox route by
`(connection, workspace, conversation, threadTs)` and, if that route has a session id, repairs the
mapping; if the route is absent but a unique session with matching connection/conversation/thread
provenance labels exists, it repairs from that session. Only after both recovery sources are absent is
the thread treated as unbound. This makes a crash after launch but before `BindAsync` recoverable by
either root redelivery or the next thread reply.

**Rationale:** the DM mapping (524 D1) is a *current-session* concept whose upsert swaps on a New
task and whose unique key is `(ConnectionId, DmConversationId)`. Thread binding is append-once-per-
`(connection, workspace, channel, thread)` with no swap. Overloading the DM store would either
prevent multiple Agents per thread (its unique key collapses them) or risk a thread reply swapping a
"current" session that threads do not have. The inbox/outbox/DM-mapping trio established the per-connection
infrastructure pattern; a fourth store of the same shape is consistent. The inbox route and Session
labels are durable recovery facts already owned by the Server, so reconciliation adds no second
authority.

**Alternatives:** (a) Reuse `SlackDmSessionMappingStore` keyed on `threadTs` — rejected: its
New-task swap semantics and unique index are wrong for threads (above). (b) Store the binding only
on the inbox row and scan — rejected: the inbox is a per-message dedup log, not a thread index; a
follow-up would need an unindexed scan. The inbox is retained as a reconciliation source, not the
steady-state index. (c) A `SlackThreadRoutingGrain` keyed by thread — rejected for v1: the inbox
dedup already serializes same-message access; the binding is low-write (one bind per launch) and
read-once per ingress, so a relational row suffices without activation machinery.

### D3. A single classifying ingress runs the channel attribution + routing state machine

The existing `/ingress` (`Api/SlackConnectionRoutes.cs`) SHALL branch on the envelope: DMs keep
today's path (claim check → owner check → DM routing from 524); channel messages enter the channel
state machine. Classification happens **before** inbox persistence (514 D5 principle), so ignored
and ambiguous messages leave no inbox row. For the connection `C` processing a channel message, the
machine is:

1. **Acceptance gate.** A normalized `bot` or `unknown` sender is acknowledged and ignored with no
   resources. A human sender's plain channel message — i.e. not a root mention of any workspace Bot
   and not a reply in a thread with a binding — is also ignored. The `ListBindingsAsync` result from
   D2, scoped to the inbound `WorkspaceTeamId`, not a per-Connection lookup alone, determines whether
   the thread has one or several bindings.
2. **Target resolution** (using D4's workspace bot set `W` and the parsed mentions `M`):
   - `|M ∩ W| ≥ 2` → **ambiguous**; contribute to the once-only prompt (D5), trigger nothing.
   - `|M ∩ W| = 1`, target `T`:
     - `T ≠ C` → not `C`'s message; `C` stays silent (the message is cleanly addressed to `T`).
     - `T = C` → `C` is the target. Route launch if `C` is not bound in this thread (D2),
       otherwise follow-up the bound session.
   - `|M ∩ W| = 0` (no Bot mention) and the message is a thread reply:
     - thread binding list contains exactly `C` → follow-up `C`'s session.
     - thread binding list contains ≥2 Agents → **ambiguous**; contribute to the once-only prompt, trigger nothing.
     - thread binding list is empty → plain channel message → ignore (covered by gate).
3. **Owner check (D7).** Once `C` is the resolved target, require `sender == C.OwnerSlackUserId`;
   else reject with a reason and create nothing.
4. **Route.** Launch reuses `LaunchConnectionAsync` + `BindAsync`; follow-up reuses
   `AcceptFollowupAsync` + `DispatchNextAsync` (524 D4) against the bound session id. Inbox
   dedup, capacity, and the route-draft resolution (`ResolveInboxRouteDraftAsync`,
   `SlackConnectionRoutes.cs:807`) gain channel route kinds (`launch_thread`,
   `followup_thread`, `ambiguous`, `ignored`).

**Rationale:** a single classifying ingress is the established architecture (`slack-agent-connection.md:60-64`,
514 D5). The attribution decision is Server authority and must be makable without adapter
coordination. Reusing launch/follow-up inherits the existing idempotency and Turn machinery, so the
channel path adds a *decision*, not a second execution surface.

**Alternatives:** (a) A separate `/channel-ingress` route — rejected: re-introduces a second
classification locus (514 D5 rejected this). (b) Push the machine into a grain — rejected for v1:
the inbox dedup is the serialization boundary and the decision is a read + branch, not a saga.
(c) Let the adapter pre-classify — rejected: attribution needs cross-connection workspace state the
adapter does not own.

### D4. Workspace-scoped bot resolution for multi-Agent attribution

Add `AgentConnectionStore.ListBoundBotsByWorkspaceAsync(workspaceTeamId)` returning
`{ ProjectId, ConnectionId, AgentId, BotUserId }` for non-deleted, identity-bound (`BotUserId` set),
Enabled Connections in that workspace. The ingress uses it to compute `W` (step 2 of D3): the set
of workspace Bots, then `M ∩ W` (mentioned ids that are our Bots). This is what distinguishes a
single-Agent mention from a multi-Bot mention and a single-Agent thread from a multi-Agent thread.

**Rationale:** "is this message addressing more than one of our Bots?" is inherently cross-Connection
within a workspace, and the Server is the only component that sees all Connections. The read is small
(few Connections per workspace) and lives behind the existing scoped store.

**Alternatives:** (a) Have each adapter report only its own mention and a central coordinator count
— rejected: re-couples attribution to adapter-to-adapter coordination. (b) Resolve bots by calling
Slack `users.info` per mention — rejected: the bound `BotUserId` is already trusted identity; no
extra Slack call is needed.

### D5. Once-only ambiguous prompt via a workspace-scoped prompt-dedup store

Because each mentioned Connection receives the Slack event independently (each App has its own
Socket Mode connection), an ambiguous message produces one ingress call per mentioned Connection.
Without a connection-agnostic dedup, each would post its own choose-one prompt, violating "只提示
一次" (AC4). Add `Infrastructure/Slack/SlackAmbiguousPromptStore.cs` with a row uniquely indexed on
`(WorkspaceTeamId, ConversationId, MessageTs)` → `{ PromptedAt, MentionedConnectionIds, ThreadTs? }`.
`TryClaimAsync(...)` is a first-writer-wins `INSERT ... ON CONFLICT DO NOTHING`: the winner enqueues
the choose-one prompt (naming the mentioned Agents/Bots) via its own outbox and acks; losers observe
the row exists and no-op. The winning delivery copies the inbound `ThreadTs`: a root message is sent
as a channel root reply, while an ambiguous thread reply is sent in that same thread. The store is
connection-agnostic; its short-lived advisory rows do not participate in per-Connection cleanup.

The prompt is advisory: after it, the user re-`@`s a single Bot, whose ingress then sees
`|M ∩ W| = 1, target = self` and proceeds normally (D3).

**Rationale:** the outbox dedups by `(ConnectionId, DispatchRef)`, so it can dedup *within* a
Connection but not *across* Connections. A workspace-scoped row keyed on the message identity is the
smallest mechanism that collapses N independent ingress calls to one prompt. Letting the race-winner
post from its own outbox reuses the existing per-Connection drain (each Bot posts with its own token);
no workspace-scoped sender is needed.

**Alternatives:** (a) Accept one prompt per mentioned Bot — rejected: violates AC4. (b) A
workspace-scoped outbox drained by a shared sender — rejected: there is no shared Bot token, and the
outbox's per-Connection ownership is load-bearing for `chat.postMessage` auth. (c) Elect a single
"coordinator" Connection per workspace — rejected: adds election machinery for a prompt-only concern.

### D6. Thread identity flows through launch origin, labels, terminal delivery, and the outbox; the adapter posts into threads

Extend the connection-origin/delivery path so acks, results, and provenance carry the thread:

- `ConnectionLaunchOrigin` (`Agent/Services/IAgentLauncher.cs:196`) gains a nullable
  `[property: Orleans.Id(5)] string? ThreadTs`. The existing conversation field carries the channel
  id for channels; since the codebase has no compat constraint, rename `DmConversationId` →
  `ConversationId` across the connection-launch subsystem (origin record, coordinator plan
  fingerprint at `AgentLaunchCoordinatorTypes.cs:162-175`, lineage at `AgentJobLineage.cs:107-122`).
  The idempotency key `slack:{team}:{conversation}:{messageTs}` is **unchanged** — `messageTs` is
  the actual message ts, unique per message even inside a thread.
- `AgentLaunchCoordinatorGrain` (`:316-320`) stamps a new `mohist.io/slack-thread-ts` label
  (add `AgentSessionQueryMetadataKeys.SlackThreadTs`) and sets `SlackConversationId` = channel id on
  the channel path.
- `SlackTerminalDelivery` (`SlackTerminalDeliveryHandler.cs:106`) and
  `AgentJobLineage.BuildTerminalDeliveryEnvelope` (`:107-122`) carry `threadTs`; the handler
  (`:40-47`) passes it into the outbox draft.
- `SlackOutboxDraft` (`SlackOutboxModels.cs:13`) and `SlackOutboxEntry` gain an optional `ThreadTs`;
  the TS `Delivery` (`mohist-slack/src/types.ts:27`) carries `threadTs`.
- The adapter drain (`mohist-slack/src/adapter.ts:127`) passes `thread_ts` to `chat.postMessage`
  when present, so acks and terminal results land in the thread rather than the channel root.

**Rationale:** acks/results must land in the thread (AC1) and provenance must record the thread
(AC7). Carrying thread identity on the same origin/delivery path that already carries the
conversation id is deterministic and avoids a binding-lookup race in the delivery handler. DMs leave
`ThreadTs` null, so the DM path is unchanged in behavior.

**Alternatives:** (a) Re-resolve the thread from a binding lookup at delivery time — rejected: adds
a store read and a race to the terminal handler; the origin already holds the thread. (b) Keep the
`DmConversationId` name — rejected: it now holds a channel id on the channel path, and the misleading
name will rot; the rename is bounded and the repo has no compat burden.

### D7. Channel access is Owner-only via the existing Owner identity; no new access-policy model

The channel branch (D3 step 3) authorizes with `sender == connection.OwnerSlackUserId`
(`Agent/Domain/AgentConnection.cs:21`). A non-Owner mention or bound-thread reply is rejected with
an actionable reason and creates no Job/Session/Input/inbox row. No `AccessPolicy` field is added to
`AgentConnection`; the DM path is unchanged (still Owner-only through the claim service).

**Rationale:** the issue scopes channel invocation to Owner only; Allowlist/Anyone and stop-authority
are owned by the access-policy issue. Reusing the bound Owner identity avoids a speculative policy
model and keeps the change focused on routing/attribution.

**Alternatives:** introduce an `AccessPolicy` enum now — rejected: YAGNI; the access-policy issue
owns that surface and would have to migrate any premature model.

## Risks / Trade-offs

- **[Cross-connection prompt race (D5)]** -> first-writer-wins unique index; losers no-op. Under a
  true split-brain two prompts could appear; the prompt is advisory and the event is rare. Acceptable.
- **[Each channel message produces one ingress call per mentioned Connection (D3/D4)]** -> each call
  is cheap (classification precedes persistence); non-attributed Connections no-op without writes.
  Slack inherently fans out per-App; this is the cost of independent Bot identities.
- **[Thread binding read is not in the same transaction as the launch (D2)]** -> persist the routed
  session id before reply and reconcile a missing mapping from that route or from the unique Session
  provenance labels before classifying any reply. Fault-injection tests cover a crash after launch
  and before binding; an unmentioned reply must still continue the original session.
- **[Mention parsing depends on Slack's `<@U...>` format (D1)]** -> if Slack changes tokenization,
  attribution degrades to "no Bot mentioned" (message ignored) rather than wrong attribution — a
  safe failure direction. Documented assumption.
- **[`ConversationId` rename touches coordinator serialized state (D6)]** -> the repo has no version-
  compat constraint; coordinator plans are rebuilt from idempotent inputs on rehydrate, and there is
  no deployed state to preserve. Code-only migration.
- **[Workspace bot list is read per ingress (D4)]** -> small N per workspace; cacheable later if
  Slack ingress volume ever demands it. Not on any other hot path.

## Migration Plan

1. **Adapter envelope (D1):** `normalizeSocketEvent` + `SlackEnvelope` gain `threadTs`,
   `mentionedUserIds`, and normalized sender kind; Bot/unknown events are acknowledged and ignored.
   Outbox drain posts `thread_ts`. Add TS types + tests.
2. **Storage (D2, D4, D5):** `SlackThreadSessionMappingStore` + workspace-and-channel-scoped row,
   binding-list query, DbContext index, and EF migration; inbox route/provenance lookup for binding repair;
   `SlackAmbiguousPromptStore` + row + migration; `AgentConnectionStore.ListBoundBotsByWorkspaceAsync`.
   All additive.
3. **Ingress state machine (D3, D7):** channel branch in `/ingress`; channel route kinds; owner
   check; reuse `LaunchConnectionAsync` / `AcceptFollowupAsync`. DM path untouched.
4. **Origin/labels/delivery/outbox thread passthrough (D6):** `ConnectionLaunchOrigin.ThreadTs` +
   `ConversationId` rename; `SlackThreadTs` label and channel/thread provenance query; terminal-
   delivery + outbox `ThreadTs`; adapter `Delivery.threadTs`.
5. **Tests + docs:** fake Slack (thread events, mention lists, member directory), fake
   adapter↔Server transport, injectable `TimeProvider`; update the 实装差距 notes in
   `design/slack-agent-connection.md` (step 4 channel/thread now landing) and `docs/agent-connections.md`.

**Rollback.** Every layer is additive (new stores, fields, route kinds) or a bounded internal
rename. Revert restores the DM-only ingress (channel messages ignored again); no stored data is
rewritten and no Agent/Job/Session loses addressability.

## Open Questions

- **Choose-one prompt wording (D5):** confirm whether it names Agents, Bots, or both. Its location is
  fixed: root for a root message and the same thread for a thread reply.
- **Workspace bot-list caching (D4):** defer caching until ingress volume is measured; confirm a
  follow-up owns it if needed.
- **Thread binding lifecycle (D2):** should an idle binding be reaped after a long quiet period, or
  persist indefinitely? v1 persists; a cleanup policy is a follow-up.
- **Slack event subscription scope:** confirm the App subscribes to `message` events in joined
  channels (required for thread follow-up without re-mention) and document the scope set in Setup;
   this is the basis for "已绑定 thread 回复不必重复 mention".
- **`ConversationId` rename blast radius (D6):** confirm the rename is limited to the connection-
   launch subsystem and does not reach the DM mapping store (which stays DM-specific by name).
