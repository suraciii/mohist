## Context

Issue 516 closes a documented-but-unimplemented gap: when a user first `@Bot`s an agent in a Slack thread that already has human discussion, the agent should start from that discussion — or refuse to start. Today the first-mention branch (`SlackConnectionRoutes.cs:1177-1183` → `LaunchChannelRootAsync`) launches on the stripped mention text alone and discards all prior content. The product contract (`docs/agent-connections.md:255-265`) and Agent API contract (`design/agent-api.md:64-82`) already specify the target behavior; this design is about how to realize it.

Current state of the relevant pieces (verified in code):

- **No thread-history reading exists.** `ISlackApiClient` (`Slack/ISlackApiClient.cs:7`) has 7 read methods but no `conversations.replies`; the `mohist-slack` adapter's `SlackWebClient` (`packages/mohist-slack/src/types.ts:64`) is capped at `chat.postMessage`.
- **The Server already calls Slack read APIs directly** with the connection's decrypted bot token (`SlackSetupVerifier`, `SlackOwnerClaimService`, `SlackConnectionRoutes.ProbeOwnerAvailabilityAsync`), resolving `ISecretStore` + `ISlackApiClient` from DI. Token decryption is `SecretStoreAddress(projectId, connectionId, SecretKind.BotToken)`.
- **The launch chain has no background field.** The stripped mention text is the entire `Prompt`; it flows `AgentLauncher.LaunchConnectionAsync` → `AgentLaunchCoordinatorRequest` → plan → `EnsureInitialLaunchCommand` → `AgentSessionInputRecord`. All optional fields are null today (`AgentLauncher.cs:239`).
- **The launch fingerprint** (`AgentLaunchCoordinatorTypes.cs:186-213`) folds `Prompt` + attachment ids + the connection origin. Redelivery of a Slack message is deduped earlier by the provider inbox on `(ConnectionId, SlackMessageIdentity)`; the coordinator returns the stored plan on repeat.
- **A read-only-background precedent exists.** The runner composes parent-issue context as an explicit "read-only background" block prepended to the task (`packages/runner/src/actions/opencode.ts:42-45` `composeOpencodePrompt`), framed so it cannot override the authoritative task. Agent `Instructions` travel as a separate field and are the privileged (system-role) input.
- **Empty-mention rejection already exists** in all three first-mention branches (`SlackConnectionRoutes.cs:1152,1163,1177`).
- **Session inputs are Orleans grain state**, not separate DB rows (`AgentSessionInputRecord` is `[GenerateSerializer]` with `[Id(n)]`). The DB stores only scalar ids like `InitialInputId`.

Constraints: no real Slack / real time in tests (fakes only); the adapter is deliberately stateless and poll-driven (no Server→adapter RPC exists).

## Goals / Non-Goals

**Goals:**
- First `@Bot` in an existing thread imports the bounded, Bot-visible thread history as first-launch-only startup context; the mention message remains the explicit task.
- Stable oldest-first truncation with explicit marking in both the Slack acceptance reply and the agent input.
- Refuse the delegation (no `AgentJob`) when the bounded range cannot be read completely.
- Startup context is untrusted user input — cannot override `Instructions`/Runtime/Model/Skills or expand permissions.
- Accepted input is immutable against later Slack edits/deletes.

**Non-Goals:**
- Slack files as input (separate issue).
- Reading whole-channel history, DMs, or anything beyond the explicit thread; auto-opening URLs, parsing cloud-drive links, or expanding Agent network access.
- Uploading Mohist artifacts as Slack files.
- Guessing or inferring content missing from an incomplete read.
- Token-accurate context-window fitting (character budget is sufficient for v1).
- Moving existing Server-side Slack read APIs into the adapter.

## Decisions

### D1 — The Server reads thread history, not the adapter
Thread history is fetched by the Server via a new `ISlackApiClient.ConversationsRepliesAsync`, using the decrypted bot token, inside the first-mention launch path.

**Rationale:** The Server already calls 7 Slack read APIs directly with the bot token (`users.info`, `conversations.info`, `auth.test`, …) and holds the authoritative thread/session mapping and durability boundary. `conversations.replies` is mechanically identical. The adapter is a deliberately narrow, stateless, poll-driven forwarder whose `SlackWebClient` is capped at `postMessage`; there is no Server→adapter request/response RPC, so routing history through it would require inventing a new RPC and putting stateful fan-out in the component the design keeps thin.

**Alternative considered:** Adapter fetches history and forwards it in the ingress envelope. Rejected — the adapter exposes only `ingress`/`lease`/`claimDelivery`/`ackDelivery`, the envelope (`SlackIngressBody`) carries the single current event, and the established boundary already puts read APIs Server-side. (See Open Questions re: clarifying the boundary table in `design/slack-agent-connection.md`.)

### D2 — Background is a new optional field through the launch chain, persisted on the plan, EXCLUDED from the fingerprint
A new optional `StartupContext` flows: `ConnectionLaunchOrigin`/launcher parameter → `AgentLaunchCoordinatorRequest` → plan → `EnsureInitialLaunchCommand` → `AgentSessionInputRecord` (append-only `[Id(n)]` on each, mirroring how `Attachments`/`Provenance` were added). It is persisted on the coordinator plan (next free id after `Attachments` at `Id(24)`) so recovery replays the same snapshot. It is **not** folded into `Fingerprint`.

**Rationale:** The mention-message identity is the dedup boundary, and background is a *volatile snapshot* read at processing time — unlike `AttachmentIds`, which are caller-validated and bound before launch, so they legitimately enter the fingerprint. A plain Slack redelivery never reaches the coordinator with drifted content: the provider inbox dedups on `(ConnectionId, SlackMessageIdentity)` and the launch reservation returns `Bound`, so a duplicate mention becomes a follow-up (`SlackConnectionRoutes.cs:1490-1531`). The reason to keep background *out* of the fingerprint is recovery/replay robustness — `ResumeIdempotentAsync` or a crash replay can re-derive the snapshot, and equality must not hinge on content that is captured once and then may differ. So: persist the first-accepted snapshot on the plan for recovery, dedup on message identity, and exclude volatile content from the replay-equality check.

**Alternative considered:** Fold raw background into the fingerprint. Rejected for the redelivery-conflict reason above.

### D3 — Background reaches the agent by server-side composition into a read-only block; `Prompt` stays task-only
At dispatch time, `BuildDispatch` composes the dispatched user input as a delimited **read-only background** block (the imported discussion + truncation marker) prepended to the task `Prompt`, reusing the framing already proven by `composeOpencodePrompt`. `AgentJobInput.Prompt` and `AgentSessionInputRecord.Text` remain the task only, so the work label (derived from `State.Input.Prompt`, `AgentJobGrain.cs:1543`) reflects the task, not the discussion. The structured `StartupContext` stays on the record/envelope for audit and truncation provenance.

**Rationale:** This is exactly the existing, instruction-safe pattern: background is user-role text framed as read-only, while `Instructions` are the privileged system-role input — so the background cannot override them. Keeping `Prompt` task-only preserves a clean work label and a clear task/context distinction with no runner contract change.

**Alternative considered:** Pass `StartupContext` as a new dispatch field and have the runner render it. More structural, but extra runner surface for no behavioral gain over the proven in-band framing. Noted as a future option if structured agent-side distinction is later required.

### D4 — Truncation uses a configurable character budget, oldest-message-first, whole-message drop
The rendered discussion is bounded by a character budget (new `SlackProviderOptions` setting). When over budget, whole oldest messages are dropped until under budget, then a stable truncation marker ("N oldest messages omitted") is prepended. A pagination depth cap (max messages fetched) bounds the number of API calls independently of the character budget.

**Rationale:** A character budget is deterministic and needs no tokenizer (avoiding a model-specific, non-deterministic dependency — important for fake-based tests). Whole-message drop keeps the transcript readable. The depth cap bounds latency/cost on huge threads.

**Alternative considered:** Token-accurate budget per the agent's model context window. Rejected for v1 (tokenizer dependency, model-variant coupling); noted as a future refinement.

### D5 — Refuse on any fetch failure; a successful read of the visible set is "complete"
The reader paginates `conversations.replies` for the thread. If Slack returns an error (e.g. `missing_scope`, `not_in_channel`), is rate-limited (`ratelimited`), or the transport fails, the connection refuses: no `AgentJob` is created, no history is imported, and the user is asked to re-mention later. If the fetch succeeds and pagination completes, the *visible* set is treated as complete — messages the Bot cannot see are not "missing," they were never in scope. Deliberate truncation to fit the character budget (D4) is **not** incompleteness and does not trigger a refusal.

**Rationale:** Matches the contract ("限定范围内的历史无法完整读取时…不创建 AgentJob") and the non-goal (no guessing from partial history). Visibility gaps ≠ fetch failures.

### D6 — Background scope: all Bot-visible thread messages strictly before the mention, by timestamp
The background is every message in the thread with `ts` strictly less than the mention message's `ts`, rendered as a chronological discussion transcript. The mention message is the task; root mentions (no prior messages) import nothing.

**Rationale:** The contract says "Bot 有权看到的已有 thread 消息"; importing the visible discussion as-is is the least-surprising reading. (Whether to filter bot-authored messages is an Open Question.)

## Risks / Trade-offs

- **[Recovery/replay re-deriving a drifted snapshot]** → Background excluded from the fingerprint (D2); a plain Slack redelivery is already absorbed by the inbox/reservation before the coordinator, and for recovery the first-accepted snapshot persisted on the plan is authoritative rather than re-deriving equality from volatile content.
- **[History read latency blocks the acceptance reply]** → The read happens in the launch path before `AgentJob` creation, so the reply waits for it. Acceptable: it is a bounded, paged read. Making it async is rejected because completeness must be a *pre-launch* fact (the contract forbids launching then discovering gaps).
- **[Large threads → many paged calls / cost]** → Pagination depth cap (D4) bounds fetches; character budget bounds stored/rendered size.
- **[Character budget ≠ exact context-window fit]** → Approximate; documented as a v1 limitation. Over-budget is still safely truncated and marked.
- **[Noisy background (other bots / off-topic)]** → Bounded and framed read-only; the agent's influence is capped by its configured capabilities. Filtering is an Open Question, not a blocker.
- **[Reservation held across a failing read]** → On refusal the launch reservation must be released so a later re-mention can proceed; otherwise the thread is stuck. Implementation must release on the refuse path.

## Migration Plan

- **Additive only.** New optional Orleans fields are append-only `[Id(n)]` (no renumbering); `StartupContext` defaults to null. Existing launches that omit it are byte-for-byte unchanged (D2 preserves fingerprint/output). No DB migration — background lives in grain state and the dispatch envelope, like `Attachments`/`Provenance`.
- **Rollback:** Disable the history-read branch so a first-mention-in-existing-thread behaves as a root mention (launch on task text alone). The optional field stays null; already-accepted launches are unaffected.
- **Docs:** On delivery, update the 实装差距 notes in `docs/agent-connections.md` (line 307-309) and `design/slack-agent-connection.md` (line 165-167) to mark thread-history import as provided. No contract change — the behavior is already the documented target.

## Open Questions

1. **Default budget and depth-cap values.** Concrete character-budget default and pagination depth cap to be set in `SlackProviderOptions` (propose a generous char budget with a modest message cap; tune from spec tests).
2. **Filter bot-authored messages?** Whether to exclude other bots' (or this Bot's own) prior messages from the background, or import all visible discussion verbatim. Propose: import all visible (least surprising); revisit if noise proves harmful.
3. **Boundary-table wording.** `design/slack-agent-connection.md`'s component table implies Slack wire protocol lives adapter-side, yet 7 read APIs are already Server-side. Should the table be clarified to state that Slack *read* APIs (history, `conversations.info`, `users.info`) are Server-side, with the adapter owning the realtime event stream + outbound delivery?
4. **Refusal-reply text.** Whether the "couldn't read the full discussion; please re-mention later" reply should be owner-localized or configurable, or kept as a fixed string consistent with the other Slack rejection replies.
