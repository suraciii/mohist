## Context

Issue 514 delivers the first externally-visible Agent entry point: an Owner-only DM vertical that wraps one Mohist Agent as an independent Slack Bot. The product spec is `docs/agent-connections.md`; the binding architecture is `design/slack-agent-connection.md` (status: wip) and `design/architecture.md:195-237`. None of it is implemented today.

Current state (sourced):

- **Launch is idempotent and recoverable (issue #512, archived).** `IAgentLauncher.LaunchIdempotentAsync` (`Agent/Services/IAgentLauncher.cs:72`) takes `(agent, prompt, context, idempotencyKey, request)` and returns `AgentLaunchResult { SessionId, JobKey, InputId, TurnId, AgentId, AgentName }`. `AgentLaunchCoordinatorGrain` (`Agent/Grains/AgentLaunchCoordinatorGrain.cs:18`), keyed `agent-launch-coord/{projectId}/{idempotencyKey}` (`AgentLaunchCoordinatorTypes.cs:144`), persists one `AgentLaunchCoordinatorPlan` and drives a restart-safe `PrepareJob → EnsureInitialLaunch → SubmitJob` fence under reminder `agent-launch-coordinator-recovery`. The HTTP route requires header `Idempotency-Key` (`Api/AgentSessionLaunchRoutes.cs:239`) and accepts only `{ prompt, context }` (`:44-48`). This is the contract a Slack dispatch will call.
- **The launcher has no caller-identity / source field.** `AgentLaunchContext` (`IAgentLauncher.cs:181`) carries only `ProjectId, IssueNumber?, EpicNumber?, Repository?, WorkspacePath?, Title?`. Sessions are tagged with a `mohist.io/source-kind` label (`Sessions/Services/AgentSessionQueryMetadataKeys.cs:8`); today only `agent-launch` (manual/Web/CLI) and `workflow` exist. Routed and mention launches each got a **distinct launcher method** that encodes origin in the idempotency key and trigger labels (`AgentLauncher.cs:217-228, 344-353`) — the precedent a Slack launch follows.
- **No Connection, Slack, or provider code exists.** Repo-wide search for Slack/Connection/Bot in `packages/` returns zero domain hits. `ServiceTarget` is `{Server, Runner}` (`MohistCliCommands.Service.cs:7`); CLI has no `agent connection` group.
- **No encryption primitive exists.** `OperatorCredential` (`Infrastructure/Security/OperatorCredential.cs`) is plaintext UTF-8 at `~/.mohist/operator-token` protected only by `0600` + symlink rejection; it authorizes a single global operator token via `X-Mohist-Operator-Token`. No `IDataProtector`, no `Aes`/`RSA` usage, no `Microsoft.AspNetCore.DataProtection` reference (`Mohist.Server.csproj`). Yet `design/slack-agent-connection.md:134` mandates "App 与 Bot 凭据由 Server 加密保存".
- **Reusable persistence precedents.** Agent-domain sub-resources `RoutingRule`/`WatchEntry` are column-per-field relational rows managed by scoped stores with composite unique indexes (`Infrastructure/Data/Agent/RoutingRuleRow.cs:3`, `MohistDbContext.cs:399-440`) — the pattern for `AgentConnection`. `InboxStore` deduplicates by `SourceEventId` via a SQLite unique index (`Inbox/InboxStore.cs:36-86`) — the pattern for the provider inbox. `EventDispatcherGrain` (cluster-singleton Orleans reminder) + `EventDispatcherService` + `IDeadLetterStore` (`Events/Grains/EventDispatcherGrain.cs:15`, `Infrastructure/Events/EventDispatcherService.cs:11`) drain a durable delivery queue with backoff and dead-letter — the pattern for the outbound outbox.
- **External HTTP client precedent.** `IHermesWebhookClient` is a typed `HttpClient` registered via `AddHttpClient<...>()` (`MohistServiceRegistration.cs:125`), faked in tests by a recording client (`HermesIssueNotificationTestSupport.cs:177`). This is the shape for `ISlackApiClient`.
- **Adapter transport precedent.** The runner uses HTTP-pull for work dispatch (`POST /api/runner/{id}/poll`, `runner/src/server/connection.ts:36`) and SignalR-push only for low-latency commands (`runner-signalr.ts:106`, hub at `MohistApiRegistration.cs:56`). The design's "adapter 领取一条 / 继续领取" language (`slack-agent-connection.md:65-67`) is a pull/outbox-drain model.
- **CLI service lifecycle.** Linux = `systemctl --user` units (`SystemdServiceInstaller.cs`); Windows = scheduled tasks. The runner unit runs `node packages/runner/dist/cli.js` (`:53-69`); adding a slack target mirrors this.

## Goals / Non-Goals

**Goals:**

- Deliver the Owner-only DM vertical: install the adapter, create+configure a Connection, claim ownership, dispatch one task, receive the result in the same DM.
- Persist the four independent Connection facts (Setup progress, Desired state, Connection health, Agent Readiness) without collapsing them into one `Connected`.
- Reuse the #512 idempotent launch path so the same Slack message identity always resolves to the same SessionInput, across Slack redelivery and adapter/Server restart.
- Keep all provider state (credentials, inbox, outbox) in Server infrastructure; keep `mohist-slack` stateless; keep the adapter entering through the Connection boundary so it cannot bypass the Agent API.

**Non-Goals:**

- DM continuous conversation, a current AgentSession per DM conversation, and New task switching (deferred — each dispatched task is independent in this issue).
- In-Slack cancel/stop; Web creation/management of Connections; credential rotation, owner transfer, disable/enable/delete lifecycle verbs.
- Channel mention, thread follow-up, Allowlist/Anyone access policy (DM is Owner-only here).
- Slack files, thread-history import, link fetching, Slack native Agent experience, marketplace, multi-tenant.
- A full Agent Readiness probe (actually driving the Runner to validate executability); this issue ships a minimal config-derived Readiness sufficient for honest dispatch decisions.
- Network exactly-once delivery; public API-key model; stored-data rewrite.

## Decisions

### D1. AgentConnection is a column-per-field relational aggregate mirroring RoutingRule/WatchEntry

Add `Agent/Domain/AgentConnection.cs` (binding: `Id, ProjectId, AgentId, ProviderKind=slack, WorkspaceTeamId, AppId, BotUserId, BotName, AvatarHash?`, lifecycle: `SetupProgress, DesiredState, ConnectionHealth, HealthReason?`, audit: `OwnerSlackUserId?, CreatedAt, UpdatedAt`) and `Infrastructure/Data/Agent/AgentConnectionRow.cs` as a full column-per-field row (not a JSON blob — binding and the four facts are individually queryable). Manage via `AgentConnectionStore` (scoped, transactional CRUD on `db.AgentConnections`, mirroring `RoutingRuleStore`). DbContext config adds a unique index on `(ProjectId, AgentId, WorkspaceTeamId, DeletedAt)` where `DeletedAt IS NULL` (enforces "at most one non-deleted Connection per Agent+workspace") and indexes on `(ProjectId, AgentId)` for `list` and `Id` for `view`.

AgentConnection owns binding, access policy, lifecycle, and the four facts only — never execution definition. It is **not** a grain (matches RoutingRule/WatchEntry); its consistency window is the relational transaction. Credentials, inbox, and outbox are separate infrastructure aggregates keyed by `ConnectionId` (D2, D6).

**Rationale:** RoutingRule and WatchEntry are the established pattern for Agent-domain sub-resources that need queryable fields and composite uniqueness; a grain would add an activation boundary for no product benefit since Connection transitions are user/adapter-driven, not high-frequency.

**Alternatives:** (a) JSON-blob row like `Agent` itself — rejected: the four facts and binding must be individually indexed/queryable and the `(Agent, workspace)` uniqueness constraint needs real columns. (b) Grain-backed aggregate — rejected: no high-frequency grain-style concurrency; adds `[PersistentState]` + activation machinery for a low-write resource.

### D2. A minimal encrypted `ISecretStore` backed by AES-GCM and a master key file

Introduce `Infrastructure/Security/ISecretStore.cs` (`StoreAsync(key, plaintext) → ciphertextBlob`, `LoadAsync(key) → plaintext`, `DeleteAsync(key)`), implemented by `AesGcmSecretStore` using `System.Security.Cryptography.AesGcm`. The symmetric key comes from a master key file at `MOHIST_SECRET_KEY_PATH` / `~/.mohist/slack-master.key` (32 random bytes, `0600`, non-symlink, auto-generated on first read mirroring `OperatorCredential`'s file discipline at `:127-129,149-155`). Ciphertext blobs (nonce + tag + ciphertext) persist in a new `ConnectionSecrets` table keyed by `ConnectionId` + `Kind ∈ {appToken, botToken}`. Tokens are never logged; a redaction guard mirrors `ConfigRoutes.cs:99-104` for any field named `*token`.

**Rationale:** the spec mandates encrypted-at-rest (`slack-agent-connection.md:134`); the DB is the natural home (same backup boundary as Sessions, `:22`). A master-key file means a routine DB backup alone does not leak live Slack tokens, and the key file can be excluded from backups or managed separately. AES-GCM is in the BCL (`System.Security.Cryptography`), needs no new package, and is the first encryption primitive in the repo — `ISecretStore` gives later features (Runner creds, webhook secrets) one seam.

**Alternatives:** (a) Mirror `OperatorCredential` plaintext-in-DB with `0600`-only protection — rejected: violates the spec's "加密保存" and these are user-supplied secrets (unlike the operator token Mohist itself mints). (b) `Microsoft.AspNetCore.DataProtection` (`IDataProtector`) — rejected: new package + DI wiring, no precedent, and its automatic key ring rotation complicates the single-master-key model this issue needs. (c) Defer encryption and ship plaintext — rejected by the proposal's Impact ("Server 引入凭据加密机制") and the spec.

**Residual risk:** the master key lives on disk alongside the DB; see Risks.

### D3. Split Slack calls: Server verifies identity, adapter owns event/message transport

Two distinct Slack callers, each holding only what its responsibility needs:

- **Server `ISlackApiClient`** (typed `HttpClient`, `AddHttpClient<ISlackApiClient, SlackApiClient>()`, recording fake in tests — mirrors `IHermesWebhookClient`). Performs the **read/verify** calls using the Bot token (loaded via `ISecretStore`): `auth.test`, `bots.info`, `users.info`, `conversations.info`, `users.list` (paged for member display only). These drive Setup identity verification (workspace/App/Bot consistency + required scopes) and owner-claim membership validation. The Server does **not** post messages.
- **`mohist-slack` adapter** holds the App token (Socket Mode receive) and Bot token (`chat.postMessage` send) **in memory only**, leased from the Server through the combined `POST .../slack-connections/{c}/adapter-session` endpoint (D5): the endpoint authenticates the adapter (operator token), returns the decrypted App/Bot tokens for that Connection, **and** records the adapter heartbeat for that Connection. The adapter calls it on start and on a short interval; the Server treats a freshly recorded heartbeat as "service available" and marks the Connection's adapter-side health offline once the heartbeat goes stale (evaluated against an injectable `TimeProvider`). This couples credential lease and liveness in one round-trip. The adapter owns "receiving events, sending messages" (`architecture.md:49`) — the Slack wire protocol — and translates them to/from normalized Connection envelopes.

This honors both `architecture.md:48` (credentials in Server infrastructure) and `:49` (Slack protocol in `mohist-slack`), and the design's explicit permission for the adapter to hold "Socket 连接、当前请求和发送中的调用" as transient protocol state (`slack-agent-connection.md:82-83`).

**Rationale:** identity and access decisions are Server authority (`slack-agent-connection.md:47`); they must be makable even when reasoning about a member who has not just sent an event the adapter is processing (e.g. owner claim, Setup verification). Routing those through the adapter would make Server authority depend on adapter liveness and re-introduce a second decision locus.

**Alternatives:** route all Slack calls (including verification) through the adapter as a proxy — rejected: re-couples identity authority to adapter uptime and contradicts "Server 核对 workspace、成员与访问策略". Have the Server also post replies — rejected: posting is Slack wire protocol, which `architecture.md:49` assigns to the adapter.

### D4. A `LaunchConnectionAsync` launcher entry stamps `source-kind=agent-connection` and derives the idempotency key from the Slack message identity

Add `IAgentLauncher.LaunchConnectionAsync(AgentInfo agent, string prompt, ConnectionLaunchOrigin origin, CancellationToken ct)` reusing the origin-in-key encoding pattern shared by the routed and mention launch paths (`IAgentLauncher.cs:98, 119`). `ConnectionLaunchOrigin` carries `ConnectionId, WorkspaceTeamId, SlackUserId, DmConversationId, MessageTs`. The method derives the idempotency key as `KeyFor(projectId, "slack:{teamId}:{conversationId}:{messageTs}")` — Slack's `messageTs` is unique per message within a channel, so `(team, conversation, ts)` is a stable global identity.

`LaunchConnectionAsync` **routes through `AgentLaunchCoordinatorGrain` exactly like the manual `LaunchIdempotentAsync` path** (#512): the connection-derived key is the coordinator idempotency key, so a redelivered Slack message collapses to the same `AgentLaunchCoordinatorPlan` and therefore the same SessionInput. (The mention of routed/mention above refers only to the origin-in-key encoding pattern, not those methods' call paths.) It stamps a new `mohist.io/source-kind = "agent-connection"` label plus `mohist.io/connection-id`, `mohist.io/slack-user-id`, `mohist.io/slack-conversation-id` audit labels on the Session (extends `AgentSessionQueryMetadataKeys`). The coordinator plan gains an optional `ConnectionOrigin` field carried through the existing `AgentLaunchCoordinatorCommandEnvelope` (`AgentLaunchCoordinatorGrain.cs:411-428`).

`LaunchConnectionAsync` is **not** exposed as a separate adapter-facing route. It is invoked internally by the ingress handler (D5) after that handler has server-side classified the envelope and confirmed owner-only access; the public Web/CLI launch route is unchanged.

**Rationale:** routed and mention launches each got a dedicated method that encodes origin in the key — Slack follows the same precedent, giving Slack-originated sessions a distinguishable identity for audit and later conversation features without disturbing the manual path. Reusing `AgentLaunchCoordinatorGrain` means redelivery/restart idempotency is inherited for free.

**Alternatives:** (a) Extend `AgentLaunchContext` with an optional origin and reuse `LaunchIdempotentAsync` — rejected: the manual route's `AllowedTopLevelFields` guard (`AgentSessionLaunchRoutes.cs:44`) and the coordinator's fingerprint are tuned to the manual shape; overloading it risks leaking manual-launch semantics into connection launches. (b) Give Slack its own coordinator grain — rejected: the coordinator is a generic process manager; a second one duplicates the recovery fence.

### D5. Adapter ↔ Server transport is HTTP envelope-submit + outbox-pull (no new SignalR hub)

The adapter authenticates with the existing operator token (it is a trusted same-trust-domain local component, `slack-agent-connection.md:132`) and uses three HTTP surfaces:

- `POST /api/projects/{p}/slack-connections/{c}/ingress` — the **single** adapter-facing inbound route. It accepts one normalized envelope (event type, stable Slack identity, sender, text), **classifies server-side first**, then acks the adapter only after a definite decision (ignore / reject / accept) — matching `slack-agent-connection.md:60-64`. Classification: (i) if the DM text matches a pending, unused owner-claim code → process the claim (D7) — checked first because a pending code can only exist once Setup has reached Claim owner; (ii) else if the Connection's Setup is not Complete → reject with an actionable reason; (iii) else if the sender is not the Owner → reject; (iv) else (owner task DM) → accept. Only the accepted branch (iv) persists a provider inbox entry (D6) and then builds the `ConnectionLaunchOrigin` and calls `LaunchConnectionAsync` (D4); claims are deduped by the code's `UsedAt` (D7) and need no inbox entry, and rejected (ii, iii) or ignored events ack Slack with **no** inbox write — rejections are idempotently re-derived on redelivery, so they leave no durable record (honoring the rejection scenarios in `slack-connection-setup` req 7 and `slack-dm-dispatch` req 1). The response carries the classification result so the adapter knows whether to ack Slack as accepted/queued/rejected/ignored. The adapter does not pre-classify; it forwards every normalized event to this one route.
- `POST /api/projects/{p}/slack-connections/{c}/adapter-session` — combined credential lease + heartbeat (D3). Authenticates the adapter, returns the decrypted App/Bot tokens, and records `LastHeartbeatAt` for the Connection. Drives the `WaitingForSlackService` ↔ service-available transition: SetupProgress can advance past Waiting only while a fresh heartbeat is on record.
- `POST /api/projects/{p}/slack-connections/{c}/deliveries/claim` and `.../ack` — drain the outbound outbox (D6): claim a delivery intent, render + post it via `chat.postMessage`, ack with the Slack result. Replaceable progress is merged server-side before claim.

The adapter polls `claim` on a short interval and after every ingress, and calls `adapter-session` on start and on a short interval; it applies a local in-flight concurrency cap (D8). No SignalR hub is introduced. Adapter-side health is derived from heartbeat freshness against an injectable `TimeProvider` (a Connection whose heartbeat is stale reads as adapter-offline regardless of its stored Setup progress); this is independent of Agent Readiness and Connection health in the four-facts model.

Note: a "Runner offline / capacity full" dispatch outcome is **not** new work — it reuses the existing AgentJob queue/dispatch behavior (#512), which already holds an accepted launch as queued/pending when no execution slot is available. This issue surfaces that state honestly through the outbox; it does not build new Runner-offline detection.

**Rationale:** the design's "adapter 领取一条 / 继续领取 Server 中尚未收敛的投递" (`slack-agent-connection.md:65-67`) is a pull model, and even the runner uses HTTP-pull for work dispatch. A single classifying ingress keeps every access/identity decision Server-side (the adapter is a pure protocol translator), and folding the credential lease into the heartbeat couples two things the adapter must do on start/reconnect into one round-trip. A stateless, restartable adapter that owns no durable state fits pull cleanly; a push hub would add a connection tracker and a second recovery locus for no latency benefit (Slack DMs are not sub-second-latency critical).

**Alternatives:** mirror `RunnerHub` with a `/hubs/slack` + `SlackConnectionTracker` — rejected: over-engineering for a stateless pull adapter; can be added later if delivery latency demands, without changing the envelope contract.

### D6. Provider inbox mirrors `InboxStore`; outbound outbox mirrors `EventDispatcherGrain` + `IDeadLetterStore`

- **Provider inbox** (`Infrastructure/Slack/SlackProviderInboxStore.cs`, `SlackProviderInboxRow`): one row per `(ConnectionId, SlackMessageIdentity)` with a SQLite unique index on that identity — duplicate insert throws a constraint violation resolved to "already accepted" (`Inbox/InboxStore.cs:36-86` precedent). Bounded capacity per connection: the inbox only ever holds accepted events (D5 classifies before persisting), so the cap is checked at the accept branch — when the count of pending/unresolved inbox entries reaches the cap, a newly accepted event is refused (the adapter does not ack Slack, so Slack redelivers; if the window expires the user resends — documented in `docs/agent-connections.md`). Accepted inputs are never evicted to make room.
- **Outbound outbox** (`Infrastructure/Slack/SlackOutboxStore.cs`, `SlackOutboxRow`): durable delivery intents with `Kind ∈ {ReplaceableProgress, TerminalResult, ExplicitFailure, UserAction}`, `State ∈ {Pending, Claimed, Delivered, DeliveryUncertain, DeadLettered}`. Replaceable progress for the same `(ConnectionId, ConversationId, DispatchRef)` is merged to the latest before claim (older row superseded). Terminal/failure/user-action rows are never silently dropped; when the outbox cannot accept another non-replaceable row, the Connection's `ConnectionHealth` becomes `Degraded(Backpressured)` and ingress stops. A cluster-singleton Orleans reminder `slack-outbox-dispatcher` (mirroring `EventDispatcherGrain`) drives backoff retry; on exhaustion a row is dead-lettered (mirrors `IDeadLetterStore`) and exposed as Delivery uncertain for manual resend (D7 in `slack-provider-reliability` spec).

Both live in Server infrastructure, keyed by `ConnectionId`, outside Agent/Session domains (`architecture.md:50`). Deletion of a Connection cascades to these tables but not to Agent/Job/Session/accepted inputs.

**Rationale:** the repo already has both dedup-on-insert (Inbox) and reminder-driven delivery-with-dead-letter (EventDispatcher); reusing their shapes keeps one reliability idiom. Splitting inbox and outbox keeps ingress capacity (protects Server) decoupled from egress capacity (protects Slack/Backpressured).

**Alternatives:** a single combined "provider queue" — rejected: ingress (accept before ack) and egress (render + post + ack) have different capacity, ordering, and failure semantics.

### D7. Owner claim is a single-use hashed code validated by Server-side `users.info`

`claim-owner` generates a short-lived (default 10 min, injectable `TimeProvider`), single-use code; stores **only its hash** + expiry + `UsedAt?` on the Connection (or a side table). Regeneration sets `SupersededBy` on the prior code, immediately invalidating it. The CLI prints the code once.

The claim is completed when an inbound DM envelope carrying the code arrives: the Server looks up the unused, unexpired code, then calls `users.info` (D3) for the DM sender and accepts the claim only if the sender is a **current, regular, non-guest, non-bot, non-deactivated** member of the bound workspace and a member of no other workspace. On success: `OwnerSlackUserId` is set, `SetupProgress` → `Complete`, the code is marked used. On any failure: the DM is rejected and no Owner is established. A successful claim also proves the App can receive DMs and reply, closing the Setup loop.

**Rationale:** hashing the code means a DB read does not leak a usable claim token; tying validation to `users.info` (not display name or message text) honors "成员校验以 Slack 的稳定 workspace 身份为准" (`slack-agent-connection.md:135`). The code is a single-use bearer for ownership establishment, not a credential.

**Alternatives:** have the adapter validate membership and forward a signed claim — rejected: membership is Server authority. Use Slack's `openid.connect` — rejected: out of scope for self-host private apps and adds an OAuth flow this issue does not need.

### D8. `mohist-slack` is a new npm workspace package; CLI gains a `Slack` service target

Add `packages/mohist-slack/` (`package.json`: `name: "mohist-slack"`, `private`, `type: module`, `engines.node >=22.19.0`, `bin: dist/cli.js`; deps `@slack/socket-mode`, `@slack/web-api`; devDeps matching runner). Append `"packages/mohist-slack"` to root `package.json:10-13` workspaces so root `build`/`test` include it. The package: connects Socket Mode, normalizes events to envelopes, calls `/adapter-session` (lease + heartbeat), forwards every event to the single `/ingress` route, drains the outbox, renders + posts replies via `chat.postMessage`, reports delivery results, applies a local in-flight concurrency cap. No durable state — all recovery is "reconnect, re-lease tokens, re-heartbeat, resume pulling from Server".

CLI changes: `ServiceTarget` += `Slack` (`MohistCliCommands.Service.cs:7`); `IServiceInstaller` += `InstallSlackAsync` + 6 lifecycle methods + `IsSlackInstalledAsync`; `SystemdServiceInstaller` gains `mohist-slack.service` mirroring the runner unit (`ExecStart = node packages/mohist-slack/dist/cli.js`, `Restart=on-failure`, env `SERVER_URL` + operator token). `mo install slack` / `mo service status slack` / `mo update slack` and the `mo agent connection` subgroup (`create`/`configure`/`claim-owner`/`view`/`list`/`edit`/`delete`) are added; `configure` reads credentials from hidden input or `--credentials-file` (UTF-8 JSON `{appToken, botToken}`, validated `0600` + non-symlink, mirroring `OperatorCredential`'s file discipline).

**Rationale:** one process per Server carrying all its Connections, each with independent App/Bot creds (`slack-agent-connection.md:51-52`). Using the official `@slack/socket-mode` + `@slack/web-api` SDKs avoids hand-rolling the Socket Mode WebSocket protocol; this is the first Slack integration and the protocol is non-trivial. The runner's npm-workspace layout is the template.

**Alternatives:** (a) hand-roll Socket Mode over `ws` + fetch — rejected: protocol complexity and ongoing maintenance risk for no benefit. (b) Put the adapter in-process in the Server (.NET) — rejected: the design chose a separate TS process because Slack's first-class client is Node (`slack-agent-connection.md:20`), and a stateless separate process keeps the trust/restart boundary clean.

### D9. Minimal config-derived Agent Readiness, with Unknown as the default

The Agent domain has no Readiness concept today (`docs/agents.md:286-287` gap). This issue introduces a **minimal** Server-side derivation on the bound Agent's `AgentConfig` JSON: `NeedsSetup` if `AgentConfig` is null or lacks a `model`/`runtime`; `Ready` if both are present; `Unknown` otherwise (default for any Agent that has not been probed). Dispatch decisions: `NeedsSetup` → reject with actionable reason (Connection stays healthy); `Unknown` → accept and let the AgentJob/Turn report the real outcome; `Ready` → accept. The four-facts surface shows this Readiness independently of `ConnectionHealth`.

**Rationale:** enough to honor the spec's "Needs setup 拒绝 / Unknown 接受并等待" without building a real Runner/runtime probe, which is a larger effort. Keeping `Unknown` as the default avoids falsely claiming any configured Agent is Ready.

**Alternatives:** build the full Readiness probe now — rejected: out of scope for the DM vertical and would couple this issue to Runner/runtime introspection. Treat all active Agents as Ready — rejected: hides the Needs-setup case the spec requires to be visible.

## Risks / Trade-offs

- **[Master key file on disk alongside the DB (D2)]** -> `0600` + non-symlink + document exclusion from routine backups; key rotation/management is an explicit follow-up (Open Questions). A stolen DB alone does not yield tokens.
- **[Adapter holds tokens in memory (D3)]** -> tokens are leased, never persisted by the adapter, and the lease is scoped to the connection; process memory is the trust boundary already granted to "高权限本机组件" (`slack-agent-connection.md:132`).
- **[Provider inbox/outbox are eventually consistent with the Connection aggregate (D6)]** -> they are keyed by `ConnectionId` and cascaded on delete; the inbox's unique index makes acceptance idempotent regardless of ordering, and the outbox's per-row state machine makes delivery resumable regardless of crash point.
- **[Minimal Readiness can mislabel an Agent (D9)]** -> `Unknown` is the safe default and is shown honestly; a mislabeled `Ready` only affects whether a dispatch is attempted, after which the authoritative AgentJob/Turn result governs — Slack delivery state never overrides it (D6).
- **[Idempotency key derived from `(team, conversation, ts)` depends on Slack never reusing a ts]** -> Slack guarantees messageTs uniqueness within a channel; a key collision would require Slack to reuse a timestamp, which is outside normal platform behavior. Documented as an assumption.
- **[DM continuous conversation deferred means each DM is an independent job (Non-Goal)]** -> accepted scope; a second DM during a running task dispatches its own independent launch (spec `slack-dm-dispatch` requirement 5), and only continuous conversation / current Session / New task remain deferred to the later issue.
- **[New Slack SDK deps enlarge the supply chain]** -> both `@slack/*` packages are widely used and pinned; lockfile is the source of truth (`AGENTS.md`).

## Migration Plan

1. **Server — credentials + Connection aggregate (D1, D2):** `ISecretStore` + `AesGcmSecretStore` + master key file; `AgentConnection` domain/row/store/querier; EF migration for `AgentConnections` + `ConnectionSecrets`. Purely additive; no existing data touched.
2. **Server — Slack client + identity verification (D3, D7):** `ISlackApiClient` + recording fake; Setup verification + owner-claim code hashing/validation.
3. **Server — provider inbox + outbox (D6):** `SlackProviderInboxStore` + `SlackOutboxStore` + `slack-outbox-dispatcher` reminder + dead-letter; EF migration.
4. **Server — connection dispatch (D4, D5):** `LaunchConnectionAsync` (coordinator-routed) + `source-kind=agent-connection` labels + the adapter HTTP routes (`/ingress` classifying, `/adapter-session` lease+heartbeat, `/deliveries/claim|ack`).
5. **mohist-slack package (D8):** new workspace package; Socket Mode + envelope normalization + outbox drain + reply posting.
6. **CLI (D8):** `ServiceTarget.Slack` + installer + `mo install/status/update slack` + `mo agent connection *`.
7. **Readiness (D9):** minimal derivation on the Connection read surface.
8. **Tests + docs:** fake Slack (event ingress/ack/redelivery/member directory), fake `ISlackApiClient`, fake adapter↔Server transport, injectable `TimeProvider` (claim-code expiry, outbox retry); update the 实装差距 notes in `docs/agent-connections.md`, `docs/self-host.md`, `design/slack-agent-connection.md`, `design/agent-api.md`.

**Rollback.** Every layer is additive (new tables, new package, new routes, new CLI commands). Revert drops the new tables/routes/package; no stored data is rewritten and no Agent, AgentJob, or AgentSession loses addressability. Existing Web/CLI Agent use is untouched.

## Open Questions

- **Master key management (D2):** confirm the key-file path/permissions story and whether rotation is a follow-up issue; decide whether `mo` should surface key status/rotation in a later issue.
- **Exact required Slack scope set:** the spec lists DM/thread/member-directory capabilities but not a fixed scope list; pin the minimal scope set (e.g. `chat:write`, `im:history`, `users:read`, `connections:write`/Socket Mode app token) during implementation and document it on the Setup page.
- **Web read-only Connection view:** the proposal leaves this to a later issue; confirm whether a minimal read-only view lands here or is fully deferred (it does not block the DM vertical).
- **DM current-Session mapping:** explicitly deferred — confirm the follow-up issue owns the per-DM-conversation current AgentSession and New task semantics.
- **Full Agent Readiness probe (D9):** confirm the follow-up that replaces the config-derived Readiness with a real Runner/runtime probe; until then `Unknown` is the default for un-probed Agents.
- **Slack delayed-events / event-retention window:** decide whether to recommend/enforce enabling Slack Delayed Events in Setup guidance and how to surface "possible missed messages" after long outages (`docs/agent-connections.md` already notes the product behavior; confirm the Server-side detection threshold).
