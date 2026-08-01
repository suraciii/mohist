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
2. For each Slack file: pre-verdict size/type from envelope metadata (D3), fetch bytes Server-side (D2), ingest into a pending `AttachmentRow` (D3).
3. `ValidateAndBindAgentInputAsync(projectId, premintedSessionId-or-sessionId, premintedInputId, ingestedIds)` → per-file verdicts.
4. Pass accepted descriptors + preminted ids into `LaunchConnectionAsync` (launch) or `AcceptFollowupAsync` (follow-up) — the existing envelope/command fields.
5. Rollback newly-bound ids via `UnbindAgentInputAsync` on any downstream failure (same `rollbackAcceptedAttachments` closure Web follow-up uses).

**Rationale:** the command/grain shapes already carry these fields; the Web/CLI path already proved the pre-mint+pre-bind model. Zero new mechanism in the Session domain — "可靠优先于丰富."

**Alternative considered:** Bind attachments inside the launch coordinator grain (single-threaded → no route-level race). Rejected: grains must stay fast; Slack HTTP fetch is I/O-bound and belongs in the route/service layer (matches issue-516's `SlackThreadHistoryReader`, a service called from the route, not the grain).

### D2. Server-side file fetch via a new `ISlackApiClient` method; adapter forwards metadata only

Add `ISlackApiClient.OpenFileContentAsync(fileId, botToken, ct)` → `SlackFileContent { Stream, FileName, ContentType, Size }`. It performs `files.info` (authoritative metadata + Bot-access check + download URL) then downloads via the bearer-token-authenticated `url_private`. `SlackApiClient.PostAsync`/`HttpClient` already handle bearer auth (`ISlackApiClient.cs:66`–`77`); the download is a GET variant.

The `mohist-slack` adapter parses `files[]` from the inbound Slack event and adds a `files` array to `SlackEnvelope` (`types.ts`) carrying `{ id, name, mimetype, size }` only — **never `url_private`, never the bot token, never raw event JSON beyond these fields**. The server `SlackIngressBody` gains the matching field. The adapter makes no new Slack API call; it stays stateless and capped at `postMessage`.

**Rationale:** issue-516 precedent — Server owns all Slack reads via decrypted bot token; adapter stays a stateless translator. Centralizing the read Server-side means the token never crosses to the adapter and `url_private` is consumed and discarded inside the fetch.

**Alternative considered:** Adapter downloads content and uploads bytes to the Server (multipart, like Web). Rejected: violates the stateless-adapter boundary, forces the adapter to hold the bot token for downloads, and duplicates the fetch authority. The metadata-only envelope is the minimal cross-process contract.

### D3. New `AttachmentService.IngestProviderFileAsync`; pre-verdict from envelope metadata before the API call

Add `AttachmentService.IngestProviderFileAsync(projectId, source, fileName, contentType, size, Stream content, ct)` → `AttachmentUploadResult`. It is the Server-side analog of `UploadAsync`: writes bytes to `IAttachmentStorage`, creates a pending `AttachmentRow` (`OwnerKind = null`, `ExpiresAt = now + PendingTtl`), stamps `Source`. Reuses `SanitizeFileName`/`NormalizeContentType`/`MaxFileBytes`.

The route pre-verdicts **before** calling the Slack API: a file whose envelope `size > MaxFileBytes` is rejected `ExceedsSizeLimit`, and whose `mimetype` fails `IsAcceptableAgentInputContentType` is rejected `UnsupportedType`, **without** spending a fetch. Only files passing pre-verdict are fetched via D2; a fetch failure (403/404/network) becomes `NotReadable`. This keeps oversized/unsupported files off the network and matches the verdict reasons one-to-one.

**Rationale:** the envelope already carries Slack's authoritative `size`/`mimetype`; using them to short-circuit is efficient and keeps the verdict honest (we never claim we tried to read a 2GB file).

**Alternative considered:** A single combined fetch-then-verdict that downloads first. Rejected: downloads bytes we will reject anyway; slower and costlier.

### D4. Persist `Source` on `AttachmentRow`; `ToAgentInputDescriptor` reads it

Add a nullable `Source` column to `AttachmentRow` (default `"upload"` for existing rows and for `UploadAsync`). `IngestProviderFileAsync` sets `"slack"`. `ToAgentInputDescriptor` (`AttachmentService.cs:513`) reads `row.Source` instead of the hardcoded literal. `ValidateAndBindAgentInputAsync`'s verdict path picks it up automatically.

**Rationale:** source must survive restart and be observable on the stored record (spec: "the SessionInput observation SHALL expose that attachment's source as `slack`"). A column is the single source of truth; transient override after bind would be fragile and re-derivable only from the row.

**Alternative considered:** Keep `Source` descriptor-only, stamped by the route post-bind. Rejected: the descriptor is built inside `ValidateAndBindAgentInputAsync` from the row; a second mutation site diverges from the persisted truth and breaks the "accepted set is authoritative across restart" invariant.

### D5. Idempotency reuses the provider-inbox dedup + deterministic preminted owner; no new dedup

Slack delivers at-least-once. The existing `SlackProviderInboxStore.AcceptAsync(draft)` returns `AlreadyExisted` for a repeated message identity and is the first durable write on the inbound path. The four dispatch sites run file fetch + bind **only after** the inbox accepts a new identity; a redelivery short-circuits before any fetch. The preminted `{sessionId}/{inputId}` owner is deterministic from the message identity (D1), so even if a bind is reached twice, the second pass re-validates rows already owned by that owner (the `OwnerKind == agent-input && OwnerId == ownerId` branch in `ValidateAndBindAgentInputAsync`) rather than colliding. Orphan pending rows from a crash mid-ingest expire via the 24h pending TTL (`CleanupExpiredPendingAsync`); Slack redelivers the unacked message and re-runs the route against the same preminted owner.

**Rationale:** the text-only Slack path already relies on exactly this two-layer dedup (inbox + coordinator-key idempotency). Attachments ride the same guarantee; adding a per-file dedup mechanism would duplicate it.

**Alternative considered:** Deterministic attachment ids keyed by Slack file id (`att_slack_{fileId}`, insert-if-absent). Rejected: it makes one physical Slack file a single globally-owned attachment, so the same screenshot sent to a second session would be rejected `AlreadyBound` — surprising for users, and inconsistent with Web/CLI where each submission is a fresh attachment. Per-message ingest (fresh `att_{guid}`) matches Web/CLI semantics; the inbox dedup is the correct single guard.

### D6. Per-file result surfaced to the Slack conversation via the existing outbox

The Bot's acceptance reply already flows through `SlackOutboxStore`/`SlackTerminalDeliveryHandler`. The per-file verdict (`AttachmentResults`) is rendered into that reply: accepted files named, rejected files named with reason. No new delivery channel; the honest-surfacing spec requirement is met by what the acceptance reply already carries.

### D7. `LaunchConnectionAsync` accepts attachments + preminted ids; empty-prompt guard delegates downstream

`LaunchConnectionAsync` (`AgentLauncher.cs:200`) gains optional `attachments`/`preMintedSessionId`/`preMintedInputId`/`preMintedTurnId` parameters (mirroring `LaunchIdempotentAsync`) and threads them onto the envelope (`AgentLaunchCoordinatorCommandEnvelope` already has the slots). The empty-prompt `throw` (`:210`–`213`) is removed; the text-or-attachment rule is enforced where it already lives — `EnsureInitialLaunch` (`AgentSession.Transitions.cs:409`–`412`) rejects an input with neither text nor an accepted attachment. `RouteFollowupAsync` populates `AcceptFollowupCommand.Attachments`/`PreMintedInputId`/`AttachmentResults`.

## Risks / Trade-offs

- **[Inline fetch adds latency to the dispatch path]** -> Only files passing the D3 pre-verdict are fetched; oversized/unsupported cost nothing. Ack semantics are unchanged (custody = inbox persist, which precedes fetch). If profiling shows pressure, move fetch behind a bounded semaphore per message; the route shape stays the same.
- **[Slack rate limits / many files on one message]** -> Bound concurrent fetches per message (small cap, e.g. 4); each file is an independent verdict, so a limited/failing file becomes `NotReadable` without blocking the others.
- **[Bot lacks `files:read` scope]** -> `files.info`/download 403s → every file verdicts `NotReadable` and the Bot reply explains it. Open question whether the setup verifier should require/advertise the scope up front (see Open Questions).
- **[Same Slack file sent in two messages]** -> Two fresh attachments (two byte copies in the store). Acceptable and consistent with Web/CLI one-upload-one-attachment; avoids the surprising `AlreadyBound` from D5's rejected alternative.
- **[`url_private` / token leakage in logs]** -> The fetch method treats `url_private` as a transient secret: never logged, never stored on the row, scope-limited to the fetch call. Banned from the descriptor/observation by the existing `attachment-input-lifecycle` invariant.
- **[Migration adds a column]** -> `Source` is nullable with a safe default; backfill is `UPDATE ... SET Source = 'upload' WHERE Source IS NULL`. No downtime; old code paths keep working.

## Migration Plan

1. **Schema** — EF migration adding `Attachments.Source` (nullable string, default `'upload'`); backfill existing rows.
2. **Adapter** — `SlackEnvelope.files` is an additive optional field; envelopes without it deserialize as before (text-only). No adapter version gate required; deploy adapter and server independently.
3. **Server** — `ISlackApiClient.OpenFileContentAsync`, `AttachmentService.IngestProviderFileAsync`, `AttachmentRow.Source`, `ToAgentInputDescriptor` source read, `LaunchConnectionAsync` signature, the four `SlackConnectionRoutes` sites. All behind the natural gate: if the envelope carries no `files`, the path is today's text-only path.
4. **Runner** — no change.
5. **Rollback** — revert server; the `Source` column is harmless if unused (nullable, defaulted). Revert adapter; envelopes stop carrying `files` and the server falls back to text-only. Already-accepted `slack`-sourced attachments remain readable through the unchanged scoped fetch (they are ordinary rows in the Mohist store).

## Open Questions

- **`files:read` scope handling.** Should `SlackSetupVerifier` require/advertise `files:read` at setup so users learn the Bot can't read files before relying on it, or degrade silently (files verdict `NotReadable`, Bot reply explains)? Lean: advertise at setup, degrade at runtime — matches the "tell the user, don't pretend" principle.
- **Per-message fetch concurrency cap.** Exact value (4?) and whether it's per-message or per-Connection. Decide in implementation against Slack rate-limit guidance.
- **Bot reply granularity.** Does the acceptance reply name each file, or summarize ("2 files attached, 1 rejected: oversized")? Spec requires per-file reason visibility; exact rendering is an implementation detail of the outbox reply builder.
