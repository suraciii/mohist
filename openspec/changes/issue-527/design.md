## Context

Issue-513 delivered a provider-agnostic input attachment boundary. Every primitive the Slack path needs already exists and is exercised by Web/CLI:

- **Pending ingest** — `AttachmentService.UploadAsync` (`AttachmentService.cs:61`) writes bytes to `IAttachmentStorage` and creates an `AttachmentRow` (`OwnerKind = null`, `ExpiresAt = now + 24h`).
- **Per-file verdict + bind** — `ValidateAndBindAgentInputAsync(projectId, agentSessionId, inputId, attachmentIds)` (`AttachmentService.cs:371`) validates each id (`NotFound`/`Expired`/`AlreadyBound`/`ExceedsSizeLimit`/`UnsupportedType`/`NotReadable`), atomically claims it under the `agent-input` owner kind (`OwnerId = "{sessionId}/{inputId}"`), returns per-file verdicts.
- **Pre-mint + pre-bind** — Web launch (`AgentSessionLaunchRoutes.cs:182`–`195`) mints deterministic `sessionId`/`inputId`/`turnId` from `{projectId}\n{idempotencyKey}`, pre-binds attachments to that owner, then hands descriptors + preminted ids to the coordinator, which adopts them. Web follow-up (`AgentSessionFollowupRoutes.cs:146`–`195`) does the same against the existing session.
- **Command/grain shapes already accept attachments** — `EnsureInitialLaunchCommand.Attachments` (`IAgentSessionGrain.cs:335`), `AcceptFollowupCommand.Attachments`/`PreMintedInputId`/`AttachmentResults` (`IAgentSessionGrain.cs:229`–`253`), and `AgentLaunchCoordinatorCommandEnvelope.Attachments`/`PreMintedInputId` (`AgentLaunchCoordinatorGrain.cs:481`–`498`) all carry the fields. **These need no shape change.**
- **Runner delivery is owner-scoped** — `openAgentInputAttachment` (`packages/runner/src/server/connection.ts:421`) reads bytes only from the Mohist store via the owning `{sessionId}/{inputId}`. Provider-agnostic; needs no change.

What the Slack path lacks today:

- `AgentLauncher.LaunchConnectionAsync` (`AgentLauncher.cs:200`–`213`) rejects empty prompts ("connection launches do not accept attachments") and never sets `Attachments`/`PreMintedInputId` on its envelope.
- The four Slack dispatch sites in `SlackConnectionRoutes.cs` (DM launch `:1135`, DM follow-up `RouteFollowupAsync` `:664`/`:1177`, channel launch `:1798`, thread follow-up `:1893`) extract only `body.Text`; they pass no attachments and `RouteFollowupAsync` calls `AcceptFollowupAsync` with only `Text`/`Source`/`IdempotencyKey`/`Provenance`.
- `SlackEnvelope` (`packages/mohist-slack/src/types.ts:12`) and the server `SlackIngressBody` (`SlackConnectionRoutes.cs:2072`) carry no file metadata.
- `ISlackApiClient` (`ISlackApiClient.cs:7`) has 8 read methods, none for files.
- `AttachmentRow` (`Infrastructure/Data/Issue/AttachmentRow.cs`) has no `Source` column; `ToAgentInputDescriptor` (`AttachmentService.cs:513`–`521`) hardcodes `Source = "upload"`.

Precedent: issue-516 established that **all Slack reads happen Server-side via `ISlackApiClient` using the Connection's decrypted bot token; the `mohist-slack` adapter stays stateless and capped at `chat.postMessage`** (`SlackThreadHistoryReader` is the template). File reads follow the same rule.

## Goals / Non-Goals

**Goals:**
- Files explicitly attached to the current inbound Slack message become `SessionInput` attachments on all four dispatch paths, reusing the existing boundary unchanged.
- File content fetched Server-side via the Connection's bot token; adapter forwards metadata only.
- Per-file honest verdict (incl. Bot-unauthorized / download-failed → `NotReadable`), reported back to the Slack conversation.
- Attachment-only Slack message is a valid input; the connection-launch empty-prompt guard is replaced by the text-or-attachment rule already enforced downstream.
- `slack` source distinguished from `upload`.
- Bot token, `url_private`, and raw event payload never reach the adapter's retained fields, the attachment record, Instructions, replies, or transcript.
- Idempotent across Slack redelivery and Server restart.

**Non-Goals:**
- Files appearing only in imported thread-history startup context (issue-516's text-only import stays closed).
- Auto-fetching plain URLs / cloud-drive links (stay message text).
- Auto-uploading Mohist artifacts to Slack.
- Cross-Session shared file library.
- Changes to the runner delivery contract, the Session/Job/Coordinator grains, or the `agent-input` owner-kind semantics.
- New rejection reasons (the existing enum suffices: `NotReadable` covers Bot-unauthorized/download-failed).

## Decisions

### D1. Reuse the pre-mint + pre-bind flow on every Slack path; no new ownership path

Each of the four Slack dispatch sites mirrors the Web launch / Web follow-up pattern exactly:

1. Premint identities deterministically from the Slack message identity (the same key the launch coordinator already uses, `slack:{teamId}:{conversationId}:{messageTs}`):
   - Launch: `preMintedSessionId`/`preMintedInputId`/`preMintedTurnId` from `{projectId}\n{messageKey}` (same formula as `AgentSessionLaunchRoutes.cs:182`–`185`).
   - Follow-up: `preMintedInputId` from `{sessionId}\n{messageKey}\nfollowup-input` (same as `AgentSessionFollowupRoutes.cs:146`).
2. For each Slack file: compute the message-scoped deterministic attachment id (D5); pre-verdict size/type from envelope metadata; then fetch-if-absent (check the deterministic-id row; fetch bytes Server-side (D2) and ingest (D3) only when absent) — so redelivery neither re-fetches nor duplicates.
3. `ValidateAndBindAgentInputAsync(projectId, premintedSessionId-or-sessionId, premintedInputId, ingestedIds)` → per-file verdicts.
4. Pass accepted descriptors + preminted ids into `LaunchConnectionAsync` (launch) or `AcceptFollowupAsync` (follow-up) — the existing envelope/command fields.
5. Rollback newly-bound ids via `UnbindAgentInputAsync` on any downstream failure (same `rollbackAcceptedAttachments` closure Web follow-up uses).

**Rationale:** the command/grain shapes already carry these fields; the Web/CLI path already proved the pre-mint+pre-bind model. Zero new mechanism in the Session domain — "可靠优先于丰富."

**Alternative considered:** Bind attachments inside the launch coordinator grain (single-threaded → no route-level race). Rejected: grains must stay fast; Slack HTTP fetch is I/O-bound and belongs in the route/service layer (matches issue-516's `SlackThreadHistoryReader`, a service called from the route, not the grain).

### D2. Server-side file fetch via a new `ISlackApiClient` method; adapter forwards metadata only

Add `ISlackApiClient.OpenFileContentAsync(fileId, botToken, ct)` → `SlackFileContent { Stream, FileName, ContentType, Size }`. It performs `files.info` (authoritative metadata + Bot-access check + download URL) then downloads via the bearer-token-authenticated `url_private`. `SlackApiClient.PostAsync`/`HttpClient` already handle bearer auth (`ISlackApiClient.cs:66`–`77`); the download is a GET variant.

The `mohist-slack` adapter parses `files[]` from the inbound Slack event and adds a `files` array to `SlackEnvelope` (`types.ts`) carrying `{ id, name, mimetype, size }` only — **never `url_private`, never the bot token, never raw event JSON beyond these fields**. The server `SlackIngressBody` gains the matching field. The adapter makes no new Slack API call; it stays stateless and capped at `postMessage`. The Slack file id carried here is **transient**: it is consumed by the route to compute the opaque deterministic attachment id (D5) and is then discarded — it is never persisted on `AttachmentRow` or exposed in the observation.

**Rationale:** issue-516 precedent — Server owns all Slack reads via decrypted bot token; adapter stays a stateless translator. Centralizing the read Server-side means the token never crosses to the adapter and `url_private` is consumed and discarded inside the fetch.

**Alternative considered:** Adapter downloads content and uploads bytes to the Server (multipart, like Web). Rejected: violates the stateless-adapter boundary, forces the adapter to hold the bot token for downloads, and duplicates the fetch authority. The metadata-only envelope is the minimal cross-process contract.

### D3. New `AttachmentService.IngestProviderFileAsync` with deterministic id and fetch-if-absent; pre-verdict from envelope metadata before the API call

Add `AttachmentService.IngestProviderFileAsync(projectId, deterministicId, source, fileName, contentType, size, Stream content, ct)` → `AttachmentUploadResult`. It is the Server-side analog of `UploadAsync` with two additions:

1. **Caller-supplied deterministic id** — instead of minting `att_{Guid}`, it writes the row under the supplied `deterministicId` and is **insert-if-absent**: if a row with that id already exists, it returns the existing descriptor and writes nothing. This makes redelivery a storage-level no-op (no second byte copy, no second row).
2. **Fetch-if-absent ordering** — the route checks for an existing deterministic-id row *before* calling the Slack API (D2), and only fetches+writes when the row is absent. A redelivery therefore neither re-fetches nor re-binds (honors the spec's "SHALL NOT fetch the file content again or create additional attachment bindings").

The created row is pending (`OwnerKind = null`, `ExpiresAt = now + PendingTtl`) and stamps `Source`. The method reuses `SanitizeFileName`/`NormalizeContentType`/`MaxFileBytes`.

The route pre-verdicts **before** any Slack API call: a file whose envelope `size > MaxFileBytes` is rejected `ExceedsSizeLimit`, and whose `mimetype` fails `IsAcceptableAgentInputContentType` is rejected `UnsupportedType`, **without** spending a fetch. Only files passing pre-verdict reach the fetch-if-absent path; a fetch failure (403/404/network) becomes `NotReadable`. This keeps oversized/unsupported files off the network and matches the verdict reasons one-to-one.

**Rationale:** the envelope already carries Slack's authoritative `size`/`mimetype`; using them to short-circuit is efficient and keeps the verdict honest (we never claim we tried to read a 2GB file). The deterministic id + fetch-if-absent is required for correct redelivery idempotency (D5) — it cannot be achieved with fresh per-delivery ids.

**Alternative considered:** A single combined fetch-then-verdict that downloads first. Rejected: downloads bytes we will reject anyway; slower and costlier, and re-downloads on every redelivery.

### D4. Persist `Source` on `AttachmentRow`; `ToAgentInputDescriptor` reads it

Add a nullable `Source` column to `AttachmentRow` (default `"upload"` for existing rows and for `UploadAsync`). `IngestProviderFileAsync` sets `"slack"`. `ToAgentInputDescriptor` (`AttachmentService.cs:513`) reads `row.Source` instead of the hardcoded literal. `ValidateAndBindAgentInputAsync`'s verdict path picks it up automatically.

**Rationale:** source must survive restart and be observable on the stored record (spec: "the SessionInput observation SHALL expose that attachment's source as `slack`"). A column is the single source of truth; transient override after bind would be fragile and re-derivable only from the row.

**Alternative considered:** Keep `Source` descriptor-only, stamped by the route post-bind. Rejected: the descriptor is built inside `ValidateAndBindAgentInputAsync` from the row; a second mutation site diverges from the persisted truth and breaks the "accepted set is authoritative across restart" invariant.

### D5. Idempotency: message-scoped deterministic attachment ids + provider-inbox dedup + deterministic preminted owner

Slack delivers at-least-once, and the Slack launch/follow-up block re-runs whenever a route has no recorded session id — i.e. across any crash between inbox-accept and `SetRouteSessionIdAsync` (`SlackConnectionRoutes.cs:1133`–`1142`), **regardless of `AlreadyExisted`**. Redelivery idempotency for attachments therefore cannot rely on the inbox alone; the per-file work must itself be idempotent. Three layers compose:

1. **Message-scoped deterministic attachment id.** For each Slack file the route computes a deterministic, **opaque** Mohist attachment id: `att_{StableToken($"{teamId}/{conversationId}/{messageTs}/{slackFileId}")}` (reusing `AgentLaunchCoordinatorCodec.StableToken`, the same primitive that mints preminted session/input ids). The id is:
   - **Stable across redelivery** of the same message → the same file maps to the same id → D3's insert-if-absent + fetch-if-absent make re-ingest a no-op (no re-fetch, no duplicate row/bytes).
   - **Scoped to the message** → the same physical Slack file sent in a *different* message maps to a *different* id → a fresh attachment, no spurious `AlreadyBound`. This is the property that makes deterministic ids safe where a bare `att_slack_{fileId}` (global per-file) would not be.
   - **Opaque** — produced by `StableToken`, it does not embed the raw Slack file id, so no Slack file identifier lands in the stored record or observation (the F4 invariant).

2. **Deterministic preminted owner.** The preminted `{sessionId}/{inputId}` is deterministic from the message identity (D1). On redelivery, `ValidateAndBindAgentInputAsync` is called with the same deterministic id and the same owner; for a row already owned by that owner it takes the `OwnerKind == agent-input && OwnerId == ownerId` branch (`AttachmentService.cs:418`–`423`), reports the descriptor as accepted, and adds nothing to `newlyBoundIds` — no duplicate binding.

3. **Provider-inbox dedup + coordinator idempotency** remain the outer guards (the text-only path's existing two-layer dedup); they collapse repeated messages to one SessionInput.

**Ack-timing assumption (load-bearing):** the Server's HTTP response to the adapter is returned *after* the launch completes (`SlackConnectionRoutes.cs:1135`–`1174`, the `return ApiResults.Ok` follows `LaunchConnectionAsync`), and the adapter does not ack Slack until the Server responds. Therefore a Server crash before the response → the adapter sees no success → Slack redelivers → the route re-runs against the same deterministic owner and the same deterministic attachment ids → D3 + layer 2 make completion idempotent. This is what satisfies the spec scenario "A restart does not lose pending file binding." Orphan pending rows from a crash mid-ingest expire via the 24h pending TTL (`CleanupExpiredPendingAsync`); a redelivery re-runs the fetch-if-absent path, which either reuses the partially-created row or creates it.

**Rationale:** the inbox dedup is necessary but not sufficient, because the launch block re-runs across the inbox-accepted / session-recorded window. Message-scoped deterministic ids make the per-file work idempotent without introducing cross-message coupling, and the preminted-owner re-validation is already supported by `ValidateAndBindAgentInputAsync` unchanged.

**Alternative considered:** (a) Gate fetch+bind on `!accepted.AlreadyExisted`. Rejected: `AlreadyExisted` does not distinguish "launch completed" from "inbox persisted but launch not yet done," so a crash in that window would silently drop the files. (b) Bind inside the launch coordinator grain. Rejected as in D1 — grains must stay fast; Slack I/O belongs in the route/service layer. (c) Bare per-file deterministic id (`att_slack_{fileId}`). Rejected: global single-owner per physical file → the same screenshot sent to a second session is rejected `AlreadyBound`, surprising and inconsistent with Web/CLI. The message-scoped variant adopted here keeps per-message freshness while gaining redelivery idempotency.

### D6. Per-file result surfaced to the Slack conversation via the existing outbox

The Bot's acceptance reply already flows through `SlackOutboxStore`/`SlackTerminalDeliveryHandler`. The per-file verdict (`AttachmentResults`) is rendered into that reply: accepted files named, rejected files named with reason. No new delivery channel; the honest-surfacing spec requirement is met by what the acceptance reply already carries.

### D7. `LaunchConnectionAsync` accepts attachments + preminted ids; empty-prompt guard delegates downstream

`LaunchConnectionAsync` (`AgentLauncher.cs:200`) gains optional `attachments`/`preMintedSessionId`/`preMintedInputId`/`preMintedTurnId` parameters (mirroring `LaunchIdempotentAsync`) and threads them onto the envelope (`AgentLaunchCoordinatorCommandEnvelope` already has the slots). The empty-prompt `throw` (`:210`–`213`) is removed; the text-or-attachment rule is enforced where it already lives — `EnsureInitialLaunch` (`AgentSession.Transitions.cs:409`–`412`) rejects an input with neither text nor an accepted attachment. `RouteFollowupAsync` populates `AcceptFollowupCommand.Attachments`/`PreMintedInputId`/`AttachmentResults`.

## Risks / Trade-offs

- **[Inline fetch adds latency to the Slack-ack path]** -> Mohist takes durable custody at inbox persist (fast), but the adapter does not ack Slack until the Server's HTTP response returns, which follows the launch — so fetch is on the Slack-ack critical path. Only files passing the D3 pre-verdict are fetched, and the fetch-if-absent check skips already-stored files on redelivery; oversized/unsupported cost nothing. If profiling shows pressure, move fetch behind a bounded semaphore per message; the route shape stays the same.
- **[Slack rate limits / many files on one message]** -> Bound concurrent fetches per message (small cap, e.g. 4); each file is an independent verdict, so a limited/failing file becomes `NotReadable` without blocking the others.
- **[Bot lacks `files:read` scope]** -> `files.info`/download 403s → every file verdicts `NotReadable` and the Bot reply explains it. Open question whether the setup verifier should require/advertise the scope up front (see Open Questions).
- **[Same Slack file sent in two messages]** -> Two attachments (two byte copies), because the deterministic id is message-scoped. Acceptable and consistent with Web/CLI one-upload-one-attachment; a bare per-file id (rejected in D5) would instead make the second submission spuriously `AlreadyBound`.
- **[`url_private` / token leakage in logs]** -> The fetch method treats `url_private` as a transient secret: never logged, never stored on the row, scope-limited to the fetch call. Banned from the descriptor/observation by the existing `attachment-input-lifecycle` invariant.
- **[Migration adds a column]** -> `Source` is nullable with a safe default; backfill is `UPDATE ... SET Source = 'upload' WHERE Source IS NULL`. No downtime; old code paths keep working.

## Migration Plan

1. **Schema** — EF migration adding `Attachments.Source` (nullable string, default `'upload'`); backfill existing rows.
2. **Adapter** — `SlackEnvelope.files` is an additive optional field; envelopes without it deserialize as before (text-only). No adapter version gate required; deploy adapter and server independently.
3. **Server** — `ISlackApiClient.OpenFileContentAsync`, `AttachmentService.IngestProviderFileAsync` (deterministic-id, insert-if-absent, fetch-if-absent), `AttachmentRow.Source`, `ToAgentInputDescriptor` source read, `LaunchConnectionAsync` signature, the four `SlackConnectionRoutes` sites (each computing the message-scoped deterministic attachment id). All behind the natural gate: if the envelope carries no `files`, the path is today's text-only path.
4. **Runner** — no change.
5. **Rollback** — revert server; the `Source` column is harmless if unused (nullable, defaulted). Revert adapter; envelopes stop carrying `files` and the server falls back to text-only. Already-accepted `slack`-sourced attachments remain readable through the unchanged scoped fetch (they are ordinary rows in the Mohist store).

## Open Questions

- **`files:read` scope handling.** Should `SlackSetupVerifier` require/advertise `files:read` at setup so users learn the Bot can't read files before relying on it, or degrade silently (files verdict `NotReadable`, Bot reply explains)? Lean: advertise at setup, degrade at runtime — matches the "tell the user, don't pretend" principle.
- **Per-message fetch concurrency cap.** Exact value (4?) and whether it's per-message or per-Connection. Decide in implementation against Slack rate-limit guidance.
- **Bot reply granularity.** Does the acceptance reply name each file, or summarize ("2 files attached, 1 rejected: oversized")? Spec requires per-file reason visibility; exact rendering is an implementation detail of the outbox reply builder.
