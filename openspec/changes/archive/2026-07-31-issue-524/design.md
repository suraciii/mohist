## Context

Issue 524 makes Slack DMs a continuous, controllable conversation surface on top of the Owner-only DM vertical that issue 514 landed. The product spec is `docs/agent-connections.md:200-273`; the binding architecture is `design/slack-agent-connection.md:96-127`.

Current state (sourced):

- **Every DM unconditionally launches a new session.** `SlackConnectionRoutes` ingress (`Api/SlackConnectionRoutes.cs:339`) always calls `LaunchConnectionAsync`, which mints a new AgentJob + AgentSession per message. There is no "current session per DM conversation" concept and no routing decision.
- **The follow-up pipeline is built and tested but gated away from Slack.** `AgentSessionGrain.AcceptFollowupAsync` (`Sessions/Grains/AgentSessionGrain.cs:518-583`), the Turn-assignment rule (`AgentSession.Transitions.cs:1087-1104`), the dispatch scheduler, and input-level idempotency (`FindFollowupInputByIdempotencyKey`, `:827-845`) all work for `agent-launch`/`workflow` sessions. But `AgentSessionQuerier.ResolveCanonicalFollowupTargetAsync` (`:319-342`) and `ResolveCancelTargetAsync` (`:344-371`) explicitly `return null` for any source kind other than `agent-launch` and `workflow` — so Slack-launched sessions (`source-kind = "agent-connection"`) cannot be followed up, cancelled, or stopped.
- **The metadata labels needed for a current-session lookup already exist.** `AgentSessionQueryMetadataKeys` defines `ConnectionId`, `SlackUserId`, `SlackConversationId` (`:15-17`), and `AgentLaunchCoordinatorGrain` stamps them on every connection-launched session (`:318-320`). But `AgentSessionQuery.QueryRowsByLabels` (`:165-208`) has no case for these keys — they fall through to `Where(_ => false)`.
- **Cancel/stop Turn control is built and tested.** `AgentSession.Transitions.cs:681-803` implements `CancelQueuedTurn`, `ClaimTurnStop`, and `ClassifyTurn` (Queued → cancel, Executing → stop, Terminal → already-ended). The HTTP routes (`AgentSessionCancelRoutes.cs`, `AgentSessionStopRoutes.cs`) drive these transitions but reject `agent-connection` sessions at the resolver.
- **Terminal delivery carries no work identity in its rendered text.** `SlackTerminalDeliveryHandler.Render` (`:61-77`) produces "Conclusion / Evidence / Next step" with no Job/Session reference. The delivery event from `AgentJobLineage.BuildTerminalDeliveryEnvelope` (`:100-128`) carries `JobKey` and stamps `agentId` in the data envelope, but the handler's `SlackTerminalDelivery` record doesn't declare it, so it's dropped on deserialization.
- **Provider infrastructure pattern is established.** `SlackProviderInboxStore` (dedup-on-insert, capacity, `ISlackConnectionProviderCleanup`) and `SlackOutboxStore` (bounded, merge-replaceable, never-drop-terminal) are SQLite-backed scoped stores keyed by `ConnectionId`, outside Agent/Session domains.

Prerequisites 514 (DM launch), 521 (follow-up semantics), and 522 (cancel/stop Turn control) are all done.

## Goals / Non-Goals

**Goals:**

- Route DM messages between launch (new work) and follow-up (continue current session) based on a per-DM-conversation current AgentSession.
- Let the Owner explicitly start new work (New task) without canceling prior work, and let prior work's late replies be distinguishable from current-session results.
- Accept and queue DMs that arrive while a Turn is executing, reporting "accepted, pending" not "running."
- Enable cancel (queued) and stop (executing) from the DM, reusing existing Turn control, with stale-entry protection.
- Preserve redelivery idempotency across both routing paths.

**Non-Goals:**

- Channel mention, thread follow-up, and multi-Agent ownership (step 4, not started).
- group DM and multi-person DMs.
- Auto-detecting whether the Owner is supplementing or switching topics.
- Presenting a full transcript or diagnostic workbench in Slack.
- Web/CLI UI for DM session control (the resolver changes enable it technically, but no UI work here).
- Changing the adapter — it remains stateless; all routing decisions are Server-side.

## Decisions

### D1. DM current-session mapping is a SQLite-backed store mirroring the provider inbox pattern

Add `Infrastructure/Slack/SlackDmSessionMappingStore.cs` with a row `SlackDmSessionMappingRow` keyed uniquely on `(ProjectId, ConnectionId, DmConversationId)` → `CurrentSessionId`. This lives in Server infrastructure alongside the inbox/outbox stores — the design doc assigns conversation mapping to Server infrastructure, not AgentConnection or AgentSession domain (`slack-agent-connection.md:81-83`). It implements `ISlackConnectionProviderCleanup` so Connection deletion cascades to the mapping without touching Agent/Session rows.

Operations:
- `GetCurrentSessionIdAsync(projectId, connectionId, conversationId)` → `string?` (read).
- `SetCurrentSessionIdAsync(projectId, connectionId, conversationId, sessionId)` — upsert, called after every launch.
- `ClearCurrentSessionIdAsync(...)` — optional, not required for v1 (sessions persist; clearing is a housekeeping concern).

An EF migration adds the table with a unique index on `(ConnectionId, DmConversationId)`.

**Rationale:** the inbox and outbox stores established the pattern for per-connection provider infrastructure: SQLite-backed, scoped, keyed by `ConnectionId`, outside the Agent/Session domain. A relational row (not a grain) matches because the mapping is low-write (one upsert per launch) and read-once per ingress — no high-frequency grain-style concurrency is needed.

**Alternatives:** (a) Store the mapping on `AgentConnection` grain state — rejected: mixes infrastructure with domain; the design explicitly separates them. (b) Query the latest `agent-connection` session by `SlackConversationId` label on the fly — rejected: "latest" is ambiguous after a New task switch (the old session may still be executing), and `QueryRowsByLabels` has no index for these keys today; a dedicated mapping is deterministic. (c) A `SlackDmRoutingGrain` keyed by conversation — rejected for v1: adds activation machinery for a low-write resource; the inbox dedup already serializes same-message access, and concurrent different-message races are benign (see Risks).

### D2. Ingress routing decision: follow-up vs launch, driven by the current-session mapping

After the existing inbox dedup (`SlackConnectionRoutes.cs:328-337`), the ingress handler gains a routing decision that replaces the unconditional `LaunchConnectionAsync` call (`:339`):

```
inbox.AcceptAsync(...)  // dedup by message identity; AlreadyExisted → return same ack
│
├─ if New task marker in text (D3):
│     strip marker → LaunchConnectionAsync → mapping.SetCurrentSessionId(new session)
│     ack: "Starting a new task." + dispatch decision
│
├─ elif mapping.GetCurrentSessionId() is null:
│     LaunchConnectionAsync → mapping.SetCurrentSessionId(new session)
│     ack: existing "Task accepted / queued" logic
│
└─ else (current session exists):
      grain = GetGrain<IAgentSessionGrain>(currentSessionId)
      accept = grain.AcceptFollowupAsync(text, source, idempotencyKey)  (D4)
      dispatcher.DispatchNextAsync(projectId, currentSessionId)
      ack: follow-up result-aware ("Continuing." / "Accepted, will continue after current step.")
```

The inbox `AlreadyExisted` check runs first and short-circuits the entire routing — a redelivered message returns the same ack without re-evaluating the mapping or re-launching. This makes the routing decision deterministic per Slack message identity.

**Rationale:** keeping the routing decision in the ingress handler (Server-side, after dedup) matches the established pattern where the ingress classifies before acting (`slack-agent-connection.md:60-64`). The adapter stays stateless — it forwards every normalized event to the single `/ingress` route, and the Server decides.

**Alternatives:** (a) A separate `/followup` adapter-facing route — rejected: re-introduces a second classification locus; the design mandates a single classifying ingress. (b) Push the decision into a grain — rejected for v1: the inbox dedup is the serialization boundary; the routing itself is a single read + one branch, not a multi-step saga.

### D3. New task is a leading keyword in the DM text

The Owner signals New task by starting the DM with a recognized marker (e.g., `new task`). The ingress handler detects the marker, strips it, and routes to launch. The remaining text becomes the first prompt of the new session. If the stripped text is empty, the handler replies with a prompt for the task (same as the existing empty-prompt rejection).

The marker is matched case-insensitively as a leading token followed by whitespace or end-of-string. This is an **explicit** action — the non-goal "自动判断用户是在补充还是在换话题" excludes the system *guessing*; a leading keyword is the Owner explicitly declaring intent.

**Rationale:** a leading keyword requires no Slack app configuration changes (unlike slash commands, which need an app-command registration) and no interactive components (unlike buttons, which need Block Kit payloads). It works through the existing stateless adapter and the existing text-based ingress.

**Alternatives:** (a) Slash command `/new` — rejected for v1: requires registering an app command in the Slack app config and handling a different event type in the adapter; can be added later without changing the routing contract. (b) A button on the Bot's reply ("Start new task") — rejected for v1: requires Block Kit interactive payloads and action routing; heavier than the DM-vertical needs. Both are viable follow-ups if user feedback shows the keyword is insufficient.

### D4. Follow-up from ingress calls the session grain directly with a Slack-derived idempotency key

When routing to follow-up (D2, current session exists), the ingress handler calls `IAgentSessionGrain.AcceptFollowupAsync` directly — it already knows the `currentSessionId` from the mapping, so it does not go through `AgentSessionFollowupRoutes` or `ResolveCanonicalFollowupTargetAsync`. The call mirrors the HTTP follow-up route's shape:

```
AcceptFollowupCommand(
    Text: prompt,
    Source: "agent-session-followup",   // same as HTTP route — turn classifier treats it as non-launch
    IdempotencyKey: $"slack:{teamId}:{conversationId}:{messageTs}")
```

The idempotency key uses the **same format** as the launch path (`AgentLauncher.cs:203`), so `FindFollowupInputByIdempotencyKey` (`AgentSession.Transitions.cs:827-845`) deduplicates a redelivered follow-up DM to the same SessionInput. After accept, the handler calls `IFollowupDispatchDispatcher.DispatchNextAsync` to pump the queued turn, identical to the HTTP route.

The ack reply is crafted from the `AgentSessionFollowupAcceptResult`:
- `TurnStatus = Queued` → "Accepted. Will continue after the current step finishes." (the input landed in a new queued turn because the executing turn's payload is sealed — `AgentSession.Transitions.cs:1087-1104`).
- `TurnStatus = Executing` (rare for a just-accepted input) → "Accepted and running."
- `AlreadyAccepted = true` → "This message was already accepted." (inbox dedup normally catches this first; the follow-up idempotency is the second layer).

**Rationale:** the follow-up machinery (`AcceptFollowupAsync`, `DispatchNextAsync`, Turn assignment, idempotency) is already built and tested for `agent-launch` sessions. The ingress handler reuses it directly — the only new wiring is reading the session ID from the mapping instead of from a URL path. Using the Slack message identity as the idempotency key inherits the same redelivery guarantee the launch path has.

**Alternatives:** (a) Route through the HTTP follow-up endpoint internally — rejected: unnecessary indirection; the ingress handler already has the session ID and the grain reference. (b) Create a separate `Source` value like `"slack-dm"` — rejected: the turn classifier (`AgentSession.Transitions.cs:917`) keys off `"agent-session-followup"` to mark non-launch turns; a new value would need a parallel classification path for no behavioral difference.

### D5. Session resolvers accept `agent-connection` source; `QueryRowsByLabels` supports Slack conversation identity

Two resolver changes:

1. `AgentSessionQuerier.ResolveCanonicalFollowupTargetAsync` (`:319-342`) and `ResolveCancelTargetAsync` (`:344-371`): add `agent-connection` to the accepted source kinds alongside `agent-launch` and `workflow`. The resolver already reads `SourceKind` from the session labels — no label changes needed, just removing the rejection.

2. `AgentSessionQuery.QueryRowsByLabels` (`:165-208`): add cases for `AgentSessionQueryMetadataKeys.ConnectionId`, `SlackUserId`, and `SlackConversationId` so sessions can be looked up by Slack identity. Currently these labels are stamped (`AgentLaunchCoordinatorGrain.cs:318-320`) but query falls through to `Where(_ => false)`.

These changes are not strictly required for the DM ingress path (which uses the mapping store directly), but they enable Web/CLI to follow up, cancel, and stop on Slack-launched sessions — closing the "second-class source" gap and making the Turn control surface consistent across all entry points.

**Rationale:** the resolvers are the single gate that determines which sessions are controllable via the unified Agent API. Accepting `agent-connection` there means no code path needs a Slack-specific bypass; the existing routes work unchanged. The query label cases are a straightforward index extension — the labels already exist in storage.

**Alternatives:** Leave the resolvers rejecting `agent-connection` and build a parallel cancel/stop path for DMs — rejected: violates "Slack 侧的取消与停止复用 Mohist 的 Turn 操作资格，不另立一套规则" (issue domain model) and creates a maintenance fork.

### D6. Cancel and stop from DM: leading keyword, implicit current-Turn resolution, reuse existing Turn control

The Owner triggers cancel/stop by sending a leading keyword (`cancel` / `stop`) in the DM. The ingress handler:

1. Resolves the current session from the mapping (D1).
2. Calls `IAgentSessionGrain.ResolveTurnControl` to find the session's active Turn — the most recent non-terminal Turn (queued or executing). If all Turns are terminal, the handler replies "There is no active work to cancel/stop."
3. Applies the operation based on `ClassifyTurn` (`AgentSession.Transitions.cs:793-803`):
   - **Queued** → `CancelQueuedTurnAsync` (cancel). For a launch Turn (queued), delegates to `IAgentJobGrain.CancelAsync` — same as `AgentSessionCancelRoutes.cs:81-94`.
   - **Executing** → `ClaimTurnStopAsync` + dispatch `CancelAgentSession` to Runner — same as `AgentSessionStopRoutes.cs:100-167`.
   - **Terminal** → "That work has already ended." (stale-entry protection).
4. Replies in the DM with the outcome (cancelled / stop-requested / stopped / already-ended).

The cancel-against-executing redirect is preserved: if the Owner sends `cancel` for an executing Turn, the reply says the work is running and a stop is required (same as `AgentSessionCancelRoutes.cs:76-79`).

Authorization is inherent: DMs are Owner-only (issue 514), so only the Connection Owner can reach the ingress handler with a task DM. No additional authorization layer is needed for v1.

**Rationale:** the Turn control logic (`CancelQueuedTurn`, `ClaimTurnStop`, `ClassifyTurn`, stale-entry protection) is already built and tested (issue 522). The DM surface adds only: keyword detection, implicit Turn resolution (the Owner doesn't know Turn IDs), and a Slack reply. Reusing the grain methods directly avoids duplicating the control path.

**Alternatives:** (a) Expose cancel/stop as HTTP routes that the ingress calls internally — rejected: unnecessary indirection; the grain methods are the authority. (b) Require the Owner to specify a Turn ID — rejected: the Owner has no way to know Turn IDs in a DM; the current work is the implied target. (c) Slash commands `/cancel` `/stop` — viable follow-up, same trade-off as D3.

### D7. Terminal delivery annotates replies with a concise work identity

Extend `AgentJobLineage.BuildTerminalDeliveryEnvelope` (`:100-128`) to include a short work label in the delivery payload — the first ~80 characters of the session's launch prompt, derived from the first SessionInput. Extend `SlackTerminalDeliveryHandler`'s `SlackTerminalDelivery` record to declare this field and `Render` (`:61-77`) to prefix the reply with it:

```
Task: {first 80 chars of original prompt}
Conclusion: ...
Evidence: ...
Next step: ...
```

The annotation is **always present**, not conditional on whether the session is still current. When only one task is active, the label is mild context; when multiple tasks overlap (after a New task switch), it lets the Owner distinguish which result belongs to which task. The late reply is delivered to the same DM conversation (the outbox is scoped by `DmConversationId`), so it reaches the right place — the label makes its **work identity** unambiguous.

**Rationale:** the delivery event already carries `JobKey` and `ConnectionId`; the missing piece is a human-readable label and surfacing it in the rendered text. Always annotating is simpler than conditionally checking the current-session mapping at delivery time (which would add a store dependency to the handler and a race between delivery and mapping update). The product spec requires "稳定的 Job / Session 标识" in replies (`docs/agent-connections.md:288-290`); the launch prompt prefix is the most recognizable identity for the Owner.

**Alternatives:** (a) Conditionally annotate only when the session differs from the current mapping — rejected: adds a store read to the delivery handler and a race; always-on is simpler and never wrong. (b) Show the raw `JobKey` — rejected: not human-readable. (c) Show a session name — rejected: Slack-launched sessions have no user-facing name; the launch prompt is the natural identifier.

## Risks / Trade-offs

- **[Concurrent first-DMs can create two sessions (D1, D2)]** -> Two different messages arriving simultaneously when no current session exists could both read "null" and both launch. The inbox dedup prevents same-message races; for different messages, the worst case is two sessions where the later-processed one becomes current. This is benign — both sessions' work continues independently, matching the New task semantics. Slack Socket Mode delivers events sequentially for a single connection, making true concurrency rare.
- **[Mapping read is not in the same transaction as the launch (D2)]** -> The mapping upsert happens after `LaunchConnectionAsync` returns. A crash between launch and upsert leaves a session without a mapping entry; the next DM would launch again. The launch itself is idempotent and recoverable (coordinator grain), so no duplicate work is created — the Owner just sees a second launch instead of a follow-up. Acceptable for v1.
- **[New task keyword could collide with natural language (D3)]** -> A message like "new task management system" would be misread as a New task command. Mitigated by requiring the marker as a standalone leading token (not a substring); the collision surface is narrow and the result (starting new work instead of continuing) is recoverable. Slash commands (D3 alternative) eliminate this entirely if it becomes a real problem.
- **[Follow-up idempotency key collides with launch key format (D4)]** -> Both use `slack:{team}:{conv}:{ts}`. This is intentional — a given Slack message identity should resolve to exactly one input regardless of path. The inbox dedup ensures a message is routed only once; the matching key format ensures that if the inbox is somehow bypassed, the session-level dedup still catches it.
- **[Terminal delivery work label truncation (D7)]** -> The first 80 chars may not uniquely identify a task if two tasks share a similar opening. Acceptable: the label is a hint, not a unique key; the `JobKey` is available for unambiguous lookup if needed later.

## Migration Plan

1. **Mapping store (D1):** `SlackDmSessionMappingStore` + `SlackDmSessionMappingRow` + EF migration. Purely additive.
2. **Ingress routing (D2, D3):** Replace the unconditional `LaunchConnectionAsync` call with the routing decision tree. Add New task keyword detection. Existing first-DM behavior is preserved (no current session → launch).
3. **Follow-up from ingress (D4):** Call `AcceptFollowupAsync` + `DispatchNextAsync` on the current session grain. Add follow-up-aware ack text.
4. **Resolver changes (D5):** Accept `agent-connection` in both resolvers; add Slack label cases to `QueryRowsByLabels`.
5. **Cancel/stop from DM (D6):** Add cancel/stop keyword detection + implicit Turn resolution + outcome reply in the ingress handler.
6. **Terminal delivery identity (D7):** Extend `BuildTerminalDeliveryEnvelope` with work label; extend `SlackTerminalDelivery` record; update `Render`.
7. **Tests:** fake Slack ingress (follow-up, New task, cancel, stop, redelivery), fake mapping store, injectable `TimeProvider` (turn-terminal timing, stale-entry); all under existing spec-test infrastructure.
8. **Docs:** update the 实装差距 notes in `design/slack-agent-connection.md` (step 3 DM continuous conversation now landed) and `docs/agent-connections.md`.

**Rollback.** Every layer is additive or behavior-extension. Revert restores the unconditional-launch ingress (every DM = independent work); no stored data is rewritten. The mapping table can be dropped; existing sessions and jobs are unaffected.

## Open Questions

- **New task keyword exact form:** confirm the leading marker (e.g., `new task` vs `/new` vs a single word) during implementation; user testing may prefer a slash command.
- **Mapping lifecycle:** should the mapping entry be cleared when the current session reaches a long-idle state, or persist indefinitely? v1 persists; a cleanup policy is a follow-up.
- **Cancel/stop keyword disambiguation:** if the Owner types "stop" as part of a normal instruction (e.g., "stop sending emails"), it could be misread as a stop command. Confirm whether a more specific marker (e.g., `/stop`) is needed, or whether leading-token matching is sufficient.
- **Web/CLI session control for Slack sessions:** the resolver changes (D5) technically enable it, but no UI work is scoped here. Confirm whether a follow-up issue owns the Web/CLI surface for `agent-connection` sessions.
