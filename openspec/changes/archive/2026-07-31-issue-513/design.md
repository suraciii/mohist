## Context

Issue 513 makes explicit file attachments a first-class part of Agent input. Today an Agent input is text-only end-to-end:

- `AgentSessionInputRecord` carries only `Text` (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.cs:486`); `AgentTurnRecord` links inputs via `InputIds` (`AgentSession.cs:504`).
- Launch body allows only `{ prompt, context }` — `AllowedTopLevelFields = { "prompt", "context" }` and empty `prompt` is rejected (`Api/AgentSessionLaunchRoutes.cs:44,129`). Follow-up body is `{ text }` only and whitespace text is rejected (`Api/AgentSessionFollowupRoutes.cs:73,176`). The grain command is `AcceptFollowupCommand(Text, Source, IdempotencyKey)` (`Agent/Grains/IAgentSessionGrain.cs:215`).
- Dispatch to the Runner is a flat string: `AgentJobInput.Prompt` (`Agent/Grains/IAgentJobGrain.cs:357`) and `FollowupDeliveryRequest.InputTexts` (`Sessions/Services/FollowupDelivery.cs:16`). The Runner reads `prompt`/`text` and composes one string via `buildExecutionEnvelope` (`packages/runner/src/runtime/execution-envelope.ts`), then calls OpenCode `session.promptAsync({ parts: [{ type: "text", text }] })` (`packages/runner/src/runtime/opencode/runtime.ts:336`) or Pi `session.prompt(text)` (`packages/runner/src/runtime/pi/runtime.ts:248`).

A partial UI already exists but is broken end-to-end: the Web launch composer uploads via the shared `POST /projects/{p}/attachments` route and inlines `[label](att:<id>)` into the prompt text (`packages/web/src/shared/ui/attachment-composer/AttachmentComposer.tsx`), but `handleLaunch` sends only `prompt.trim()` (`packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx:262`). The attachment is never bound to an owner (it stays `OwnerKind = null`, `ExpiresAt = now+24h`), there is no Agent-side content route, and the Runner never resolves `att:` — so the file is unreadable and expires. The follow-up composer has no attachment support at all; the CLI has no attach flag.

The reusable asset is the shared attachment store: `AttachmentService` (`Issue/Services/Attachments/AttachmentService.cs`) with `AttachmentRow { OwnerKind, OwnerId, OwnerIssueNumber, OriginalFileName, ContentType, Size, StoragePath, ExpiresAt }`, the project-scoped upload, the `Bind*Async` ownership pattern (issue/comment), content routes scoped to owner, and `CleanupExpiredPendingAsync` which deletes only `OwnerKind == null` rows. The Web issue path already sends an explicit `attachmentIds` array alongside the body and resolves `att:` via `MarkdownReader.resolveAttachment` (`packages/web/src/entities/issue/api/client.ts:168,183`).

Motivation and required behavior live in the proposal (`openspec/changes/issue-513/proposal.md`) and the four specs under `openspec/changes/issue-513/specs/`. This document covers how.

## Goals / Non-Goals

**Goals:**

- Make an accepted attachment a Mohist-managed input resource owned by exactly one `SessionInput`, across launch and follow-up, on Web and CLI.
- Accept attachment-only input (no text) as valid and let the turn execute without a fabricated user prompt.
- Give each submitted attachment a definitive accept/reject result, surfaced to the user before execution; never silently drop or pretend.
- Deliver accepted attachment content to the Runtime within the owning turn, as content the Agent can actually read, without leaking caller temp URLs, tokens, or raw platform events.
- Reuse the unified attachment store (upload, ownership binding, scoped content, retention) rather than building a parallel file store.

**Non-Goals:**

- Downloading arbitrary web URLs or auto-converting URLs to attachments (a URL stays message text).
- Slack file download / thread-history import (the boundary is designed to be reusable later, but Slack is out of scope here).
- A file editor, long-term file library, or cross-Agent shared asset space.
- Auto-uploading Mohist artifacts to external chat platforms.
- Changing how `Instructions`, `Runtime`, `Model`, `Variant`, or `Skills` are fixed per AgentJob.

## Decisions

### D1. Attachments live as a child record of the SessionInput, not a top-level resource

`AgentSessionInputRecord` gains an ordered `Attachments` list (new Orleans `Id` slot appended for backward-compatible deserialization). Each entry records the stable attachment id, provenance source, display name, content type, size, and acceptance. The shared `AttachmentRow` remains the storage record (reused as-is); binding sets `OwnerKind = "agent-input"` with `OwnerId` identifying the owning session+input. The SessionInput child record is the authoritative "what this input accepted"; the `AttachmentRow` is the content+retention record.

- **Alternative considered:** a top-level Attachment grain/resource addressable by id. Rejected — it invites cross-reference reuse (exactly what the `attachment-input-lifecycle` spec forbids) and duplicates the Input/Turn child-record pattern already used on `AgentSession`.
- **Why:** single-owner scoping is enforced at the aggregate; matches the existing durable child-record model; the input's accepted set is queryable exactly as Input/Turn are.

### D2. Reuse the shared AttachmentService; add an `agent-input` owner kind and a scoped content route

Upload stays `POST /projects/{p}/attachments` (project-scoped, pending, 24h TTL). At input acceptance, Mohist binds accepted ids with a new `OwnerKind = "agent-input"` (new constant alongside `OwnerKindIssue`/`OwnerKindComment`, `AttachmentService.cs:17-18`) and an owner id encoding session+input, clearing `ExpiresAt`. A new content route — scoped to the owning agent-session/input, e.g. `GET .../agent-sessions/{sessionId}/inputs/{inputId}/attachments/{attachmentId}/content` — returns content only when the attachment's owner matches, mirroring `OpenIssueContentAsync` (`AttachmentService.cs:263`). `CleanupExpiredPendingAsync` (`AttachmentService.cs:364`) already keys on `OwnerKind == null`, so bound agent-input attachments are automatically protected; pending uploads from the current broken Web path keep expiring as today.

- **Alternative considered:** a dedicated agent attachment store. Rejected — fragments retention/cleanup and violates the "unified input boundary" the issue requires.
- **Why:** one retention rule, one storage boundary, one leakage contract; minimal new storage code.

### D3. Validate-and-bind at acceptance, before turn execution; relax the mandatory-text rule

Launch/follow-up bodies accept an explicit `attachments` field (list of pending attachment ids), added to `AllowedTopLevelFields` (`AgentSessionLaunchRoutes.cs:44`). Acceptance validates every id (exists in project, not already bound to another owner, readable, within size/count/type limits), binds the accepted ones to the owning input, and returns a per-attachment result (accepted / rejected with a reason: not-found, expired, not-readable, exceeds-size-limit, unsupported-type, already-owned). The mandatory-input rule becomes "non-empty text **or** ≥1 accepted attachment." Validation+binding is persisted together with the input record so the accepted set is authoritative and survives restart.

- **Alternative considered:** defer validation into the grain transition only, or defer to delivery time. Rejected — the spec requires surfacing failures to the caller before execution and forbids starting a turn with unusable attachments; binding must be durable and consistent with the recorded input.
- **Why:** callers get an honest, synchronous per-file result; the turn never starts with a silently reduced or all-broken set; idempotent retry resolves to the same accepted set.

### D4. Delivery = workspace materialization + an honest attachment manifest, with native image parts where the Runtime supports them

The dispatch payload (`AgentJobInput` / `FollowupDeliveryRequest`) carries accepted attachment **descriptors** (id, name, type, size) plus a scoped content-fetch path — never the bytes. Before turn start, the Runner fetches each accepted attachment's content via the scoped server content route (D2) using its existing server connection, and:

1. Writes the file into the Agent workspace under a stable path `…/<workDir>/.mohist/attachments/<inputId>/<id>/<name>`, so the coding Agent reads it with its file tools (works for OpenCode and Pi).
2. For image content types on a Runtime whose prompt API accepts image parts (OpenCode `parts`), additionally passes the image as a native image part so the model can see it.
3. Passes an **honest, machine-attributed attachment manifest** — a clearly labeled block listing the provided files (name, type, size, workspace path) — as turn content. This block is attributed to the system and states facts; it never impersonates the user and never invents a task.

For an attachment-only input (no user text), the manifest block is the turn-initiating content. This is not a "fabricated prompt": it is factual, visible, system-attributed metadata about the input (the same framing already used by the `[mohist-execution-definition]` block in `buildExecutionEnvelope`), so the Agent knows what was provided without Mohist pretending the user said something.

- **Alternative A:** inline all attachment bytes in the dispatch payload. Rejected — heavy for large/binary files and bloats the dispatch/queue.
- **Alternative B:** native parts only, no workspace write. Rejected — Pi and coding agents are workspace-centric; documents/code must be readable as files, not just passed as parts.
- **Why:** the Agent genuinely reads actual content (`agent-attachment-delivery` spec); runtime-agnostic; large files handled by streaming via the scoped route; leakage-free (the Runner fetches through the owning-input scope and never receives caller temp URLs/tokens).

### D5. Web follows the existing issue pattern; CLI gains `--attach`

- **Web launch:** extract an explicit `attachmentIds` array (reuse `extractAttachmentIds`) and send `{ prompt, context, attachments }`; stop relying on inline `att:` text. Resolve `att:` references for preview/render via `MarkdownReader.resolveAttachment` pointed at the scoped agent-input content path.
- **Web follow-up:** add `AttachmentComposer` to `SessionFollowupComposer` and send `{ text, attachments }`.
- **CLI:** add a repeatable `--attach <path>` to `mo agent launch` (`MohistCliCommands.Agent.cs:631`) and the follow-up command (`MohistCliCommands.Session.cs:225`); upload each file via `POST /attachments`, collect ids, send `attachments`, and print the pending list and per-file acceptance result.

- **Alternative considered:** keep inline `att:` text and just add resolution. Rejected — the proposal explicitly replaces the broken inline path with explicit ownership.
- **Why:** consistency with the proven issue flow; one client contract across Web and CLI.

## Risks / Trade-offs

- **[A Runtime cannot natively "see" images]** -> Baseline delivery is workspace materialization (Agent reads via tools); native image parts are additive per-Runtime. Document that an image is always *available as a file*, and is *visible to the model* only when the Runtime supports image parts.
- **[Acceptance-time read succeeds but delivery-time read fails]** -> Validate readability at acceptance; if a delivery-time fetch fails, the turn reports that attachment as unavailable (an honest, surfaced failure) and never reports it as delivered. Rejected attachments are excluded from the turn.
- **["no fabricated prompt" vs a Runtime that requires non-empty text to start a turn]** -> Use the system-attributed, factual manifest block (D4), never user-impersonating text. The contract is: Mohist may state *what was attached*; it may not invent *what the user asked*.
- **[Large attachments stall turn start or bloat the workspace]** -> Dispatch carries descriptors only; the Runner streams content via the scoped route; enforce `MaxFileBytes`/`MaxCountPerOwner` at upload and acceptance (extend to agent-input owner if separate limits are desired).
- **[Workspace files leak across inputs/sessions]** -> Materialize under a per-`inputId` path; the workspace is per-run and the server-owned scoped content route remains the source of truth; another session cannot fetch an attachment by bare id (D2).
- **[Orleans record compatibility]** -> Append the new `Attachments` member at a fresh `Id` slot with a default; existing persisted sessions deserialize unchanged.
- **[Launch route rejects unknown top-level fields]** -> Adding `attachments` to `AllowedTopLevelFields` is the forward gate; rollback is simply removing it from the allowlist (text-only clients are unaffected).

## Migration Plan

Purely additive — no change to existing text-only inputs.

1. **Server:** add `agent-input` owner kind, scoped content route, and the `attachments` field to launch/follow-up bodies + `AllowedTopLevelFields`; extend `AgentSessionInputRecord`; validate-and-bind in the acceptance pipeline; carry descriptors in the dispatch payload.
2. **Runner:** fetch accepted attachments via the scoped route, materialize into the workspace, emit the manifest block, and pass native image parts where supported.
3. **Web:** send explicit `attachmentIds` on launch; add attachment support to follow-up.
4. **CLI:** add `--attach` and result reporting.

**Rollback:** clients stop sending `attachments`; the server ignores the field (remove from allowlist). Pending uploads (`OwnerKind = null`) created by the legacy broken Web path continue to expire on their 24h TTL as today — no data migration is needed, since they were never readable.

## Open Questions

- Exact OpenCode `parts` image/file schema and the supported content types for native image delivery (confirm against the installed `@opencode-ai/sdk`); whether Pi supports any native attachment/image input or is workspace-only.
- Content-fetch auth model for the Runner: reuse its existing server-connection identity against the scoped route, or have the dispatch carry a short-lived scoped token? (Lean: runner identity + scoped route.)
- Limits: reuse the shared `MaxCountPerOwner` / `MaxFileBytes` for agent-input owners, or define separate per-input limits?
- API field naming: `attachments` vs `attachmentIds` for consistency with the issue body (which uses `attachmentIds`).
