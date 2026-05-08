## Context

The coder session page already has a dedicated route, a transcript assembler, a session-detail API, a live transcript hook, and basic tool cards. The remaining problem is not missing plumbing; it is that transcript normalization and presentation responsibilities are split across backend assembly, live SSE handling, and frontend cards in ways that leak raw event-log details into the user experience.

The current backend returns `turns` assembled from `session_stream_log` when available, with workflow-log fallback for older sessions. `SessionTranscriptAssembler` handles prompts, text chunks, reasoning chunks, tool starts/updates, and errors, but ordering is timestamp-only, tool identity inference is shallow, metadata is minimal, and tool/file summaries are not first-class. The frontend then converts `ToolPart` into older `ToolCallEntry` cards and the live hook manually mutates a similar-but-not-identical shape, which is the main source of live versus refresh divergence.

The design constraint is to keep coder execution semantics and raw audit data unchanged while making the normalized transcript the single interface the UI reads. Raw prompts and raw payloads remain available behind disclosure controls, but the default surface becomes a readable Mohist/Coder conversation.

## Goals / Non-Goals

**Goals:**

- Produce one stable transcript shape for historical replay and live streaming.
- Make `SessionTranscriptAssembler` responsible for ordering, turn boundaries, tool identity, tool merging, prompt summaries, file summaries, warning metadata, and aggregate counts.
- Render Mohist prompts as readable summaries with raw XML/task prompt content collapsed by default.
- Render Coder responses and assistant parts in natural reading order, with context tools grouped and file-changing tools summarized by file.
- Surface session status in user terms: loading, live, finalizing, completed, failed, stale, and legacy/incomplete where relevant.
- Preserve raw prompt and raw tool payloads for audit/debugging without making them the primary UI.
- Add regression tests for the #143 failure modes: same-second ordering, nested/no-id tool merging, title/input-based tool inference, live/refetch parity, apply_patch summaries, error rendering, and auto-scroll behavior.

**Non-Goals:**

- No composer or continue-conversation behavior on the session page.
- No change to how opencode/coder executes tasks or which tools are allowed.
- No full clone of opencode side panels, terminal tabs, review tabs, or composer UI.
- No removal of raw prompt, raw tool input, raw output, or legacy workflow logs.
- No redesign of the issue detail workflow board beyond links/header data that point into the session page.

## Decisions

### D1: Backend Owns The Canonical Transcript Shape

`SessionTranscriptAssembler` will become the canonical normalization boundary for session replay. The API should return `metadata`, `turns`, and derived transcript fields that the UI can render directly instead of forcing every frontend component to re-parse raw logs.

The transcript model should be extended in a backward-compatible way:

- `SessionMetadata.lastActivityAt`, `eventCount`, `toolCount`, `turnCount`, `changedFiles`, `warnings`, `hasUnknownTools`, and `statusKind`.
- `SessionTurn.user.summary` with `title`, `subtitle`, `outputPath`, `contextFiles`, `kind`, and `rawText` or equivalent raw prompt field.
- `ToolPart.tool.normalizedName`, `displayTitle`, `displaySubtitle`, `category`, `rawInput`, `rawOutput`, `changedFiles`, and optional `warnings`.
- `FileChangeSummary` entries with `path`, `operation`, `additions`, `deletions`, optional `oldPath`, and optional raw patch/detail.

The API can keep existing fields such as `turn.user.text`, `tool.toolName`, `tool.input`, and `tool.output` during implementation to minimize churn, but new UI should prefer the normalized fields.

**Alternatives considered:**

- Do all normalization in React. This keeps backend changes smaller but repeats parsing logic across historical and live paths and makes parity hard to prove.
- Add a separate `/transcript-v2` endpoint. This avoids touching current response shape but creates duplicate APIs for the same concept. Extending the existing session detail response is simpler and keeps callers converging on one contract.

### D2: Deterministic Event Ordering Uses Time, Event Priority, Then Stable Id

Assembler sorting will use a deterministic comparator: `createdAt` timestamp first, then event-type priority, then insertion/id fallback. Same-second events must preserve conversation meaning even when SQLite timestamps have equal precision.

Recommended event priority within the same timestamp:

- `mohist_prompt` before assistant activity because it opens a turn.
- `agent_thought_chunk` and `agent_message_chunk` before related tools when their ids or insertion order indicate they arrived first.
- `tool_call` before `tool_call_update`.
- recovery/error events before terminal completion when both occur at the same time.
- terminal events last so they close the current turn after all emitted parts are attached.

If the repository row id encodes insertion order, use it as the final tie breaker. If not, preserve input array order by adding an index before sorting.

**Alternatives considered:**

- Timestamp-only sorting. This is the current behavior and fails when multiple ACP events share a timestamp.
- Trust event ids only. This is brittle because persisted ids may be UUID-like and not chronological.

### D3: Tool Normalization Is A Small Pipeline, Not Scattered Conditionals

Introduce a single internal normalization path for tool payloads before merging starts/updates. The normalizer should flatten both top-level payloads and nested `toolCall` payloads into one `NormalizedToolEvent`:

- Identity fields: `toolCallId`, `normalizedName`, `sourceName`, `title`, `status`, timestamps.
- Payload fields: `rawInput`, `rawOutput`, `metadata`, `error`.
- Display fields: `displayTitle`, `displaySubtitle`, `target`, `category`.
- Merge hints: `correlationKey` for no-id start/update pairing.

Name inference order should prefer explicit and reliable fields first: `toolName`, `name`, known title values such as `apply_patch`, raw input shape (`patchText`, `command`, `pattern`, `filePath`, `todos`), raw output metadata/tool fields, then a controlled `unknown` fallback. Unknown should only appear when no supported signal exists; inferable payloads should instead produce a warning if confidence is low.

No-id tool merge should use a per-turn pending queue keyed by normalized tool name and, when available, target/path/title. This avoids pairing two concurrent `read` calls only by name when their targets differ.

**Alternatives considered:**

- Continue extending `parseToolCallStart` and `parseToolCallUpdate` separately. That duplicates inference and is why update-only events can still become `unknown`.
- Require upstream SSE to always send perfect `toolCallId` and `toolName`. That would be ideal but does not solve historical sessions and real ACP payload variance.

### D4: Prompt Summary Is Derived From Persisted Metadata First, Raw Text Second

Mohist prompt rendering should not show full `<mohist-task>` XML by default. The assembler should expose prompt summary fields from persisted prompt metadata when available: `kind`, `title`, issue number/title, role summary, output path, context files, and task headline.

For existing raw-only prompts, derive a best-effort summary by parsing the structured sections already present in Mohist prompts:

- Prefer `<role>` as the prompt title when short and human-readable.
- Prefer `<contract>` output path for subtitle/artifact output.
- Prefer issue number/title from prompt text or session metadata when available.
- Preserve the complete raw prompt as the disclosure body.

Prompt parsing should be tolerant string extraction rather than a strict XML parser, because prompts may contain markdown and code fences that are not valid XML documents.

**Alternatives considered:**

- Add a strict prompt schema and migration before UI work. This is cleaner long-term but blocks fixing existing sessions and is unnecessary for read-only summarization.
- Only summarize in the frontend. This repeats parsing and leaves API consumers without readable prompt metadata.

### D5: Context Grouping Is A Presentation Pass Over Normalized Parts

The persisted transcript should continue to store individual tool parts. The UI should apply a presentation grouping pass over each turn's assistant parts to collapse consecutive context-gathering tools into a synthetic display group.

Context tools include `read`, `glob`, `grep`, `list`, `membrowse`, `memread`, `memsearch`, and closely related aliases. A group should show counts and representative targets, such as `Read 8 files · Searched 3 patterns`, and expand to the individual tool cards/raw payloads.

Do not group across text/reasoning/error/file-change boundaries. Tool order is still meaningful; grouping is only a visual compression of adjacent context collection.

`todowrite` should render as a compact summary (`Updated todo list`) with details hidden by default, not as a noisy context group item.

**Alternatives considered:**

- Have the backend emit grouped parts. This makes the API easier for this UI but loses the exact assistant part sequence for other consumers and complicates live incremental updates.
- Hide context tools entirely. This improves readability but removes auditability, which is explicitly required.

### D6: File Change Summaries Are Extracted Once And Rendered As First-Class Results

File-changing tools (`apply_patch`, `edit`, `write`, and supported aliases) should be parsed into `changedFiles` summaries during tool normalization. The parser should handle:

- `apply_patch` `patchText` envelopes with `Add File`, `Update File`, `Delete File`, and `Move to` headers.
- write/create payloads with file path/content.
- edit payloads with old/new strings where additions/deletions can be estimated by line counts.
- failed tools, where intended file targets can still be shown but operation is marked failed.

The frontend should render a `Patch`/`Files changed` block as the default view, with raw patch/input/output collapsed below it. Session-level metadata should aggregate changed files across turns so the header can show artifact or file summary when useful.

**Alternatives considered:**

- Render raw patches only with syntax coloring. This is close to current behavior and still forces users to parse implementation detail before understanding the result.
- Compute file changes from git diff. That may be useful elsewhere but can be wrong for historical sessions after additional edits, rebases, or cleanup.

### D7: Live Updates Should Reuse Transcript Semantics, Not Reimplement A Parallel Assembler

The live hook should stop growing a separate transcript model that guesses at ids, names, and part shape. The preferred path is to introduce shared frontend helpers that apply the same normalized part semantics as the API response, using SSE events only as incremental updates until the detail query refetches.

On `coder_session_completed`, the hook should invalidate/refetch detail and reconcile by replacing local live state with the canonical API transcript. During a run, live append should use stable ids from SSE when present and the same tool normalization fallback rules used for historical events.

The page should track whether the user is near the bottom. New text, tool updates, recovery updates, and completion updates should mark `newContentAvailable` when the user is not near the bottom, without forcing scroll. Clicking `Jump to bottom` restores follow mode and clears the marker.

**Alternatives considered:**

- Stream fully assembled transcript parts from the backend. This is attractive but requires a larger SSE contract change. The smaller step is to make live append compatible with the existing detail response and rely on refetch for canonical convergence.
- Always refetch on every SSE event. This simplifies parity but is too expensive and would degrade streaming responsiveness.

### D8: Session Status Is Derived From Activity And Runtime State

The header should display a user-facing `statusKind`, not just raw `coder_session.status`. The API can provide a derived value using session status, terminal timestamps, last activity, and current running state where available.

Suggested status derivation:

- `loading`: detail query not resolved yet.
- `live`: raw status is `running` and recent activity exists or the session is known active.
- `finalizing`: raw status is `running`, completion SSE was seen or issue moved stage, but canonical terminal status has not refetched yet.
- `completed`: terminal success.
- `failed`: failed, timeout, or cancelled.
- `stale`: raw status is `running` but last activity is older than the stale threshold and no active agent status matches.

The frontend can refine `finalizing` with local SSE knowledge, but the API-owned metadata should be sufficient for historical rendering.

**Alternatives considered:**

- Display raw database status only. This is simpler but does not explain common user states like finalizing or stale.
- Derive all status in the frontend. That hides useful metadata from other clients and duplicates date/status logic.

## Risks / Trade-offs

- [Risk] The transcript response grows and duplicates raw plus normalized fields. → Mitigation: keep raw fields only where needed for audit disclosures, and prefer compact summaries for default rendering.
- [Risk] Tool name inference may misclassify unusual custom tools. → Mitigation: use conservative inference, attach transcript warnings, and keep raw payload available for audit.
- [Risk] No-id tool update pairing can be ambiguous when multiple same-name tools run concurrently. → Mitigation: key pending queues by normalized name plus target/title where possible, and warn when pairing falls back to name-only.
- [Risk] Prompt summary extraction from raw task XML can be imperfect. → Mitigation: prefer persisted metadata when available, use tolerant best-effort extraction for legacy data, and always expose the raw prompt.
- [Risk] Live append can still diverge temporarily before refetch. → Mitigation: use shared normalization helpers for live events and replace with canonical API transcript after completion/refetch.
- [Risk] File additions/deletions from edit/write payloads are estimates, not guaranteed git stats. → Mitigation: label them as tool-level summaries and keep raw patch/input for exact audit.
- [Risk] Context grouping could hide important tool failures. → Mitigation: groups must show failure counts/status and expand to failed tools; errors should break or prominently mark groups.

## Migration Plan

1. Extend backend transcript types and assembler internals without removing existing `turns`, `user.text`, `tool.toolName`, `tool.input`, or `tool.output` fields.
2. Add deterministic sorting, prompt summary extraction, tool normalization, tool merge improvements, file summary extraction, transcript metadata, and warnings in `SessionTranscriptAssembler`.
3. Update `GET /api/issues/:number/coder-sessions/:sessionId` to pass through enriched transcript metadata and include `mohist_prompt` in any fallback event set where safe.
4. Update shared frontend types to include new optional metadata, prompt summary, normalized tool fields, changed files, warnings, and status kind.
5. Refactor `SessionTranscriptView` into a conversation renderer with explicit Mohist/Coder labels, prompt summary/raw disclosure, assistant part rendering, context grouping, todowrite summary, file change cards, and improved errors.
6. Refactor `ToolCallCard` helpers so title/subtitle fallback, context summaries, and file-change summaries consume normalized tool fields first and raw payloads second.
7. Update `useSessionTranscript` so live events append compatible normalized parts, respect near-bottom/new-content behavior for all event types, and converge to refetched detail after terminal events.
8. Add backend and frontend regression tests before or alongside implementation. Use realistic fixtures for multi-turn plan sessions, large prompts, title-only `apply_patch`, no-id nested tools, context bursts, errors, and live/refetch parity.
9. Rollback is low risk because execution semantics and persistence remain unchanged. If UI regressions appear, the API can continue serving enriched data while the frontend falls back to existing raw turn/tool rendering.

## Open Questions

- What exact stale threshold should the session header use for a running session with no recent activity: 2 minutes, 5 minutes, or the existing agent heartbeat/recovery threshold?
- Should transcript metadata include issue title/number directly, or should the session page continue combining session detail with `useIssue()` data?
- Should changed-file summaries for plan sessions identify OpenSpec artifact types (`proposal`, `spec`, `design`, `tasks`) in the backend, or should that remain a frontend label derived from paths?
