## Context

The session page already has the right major pieces: persisted session stream logs, `assembleSessionTranscript()`, a session-detail API, `useSessionTranscript()` for live SSE updates, `SessionTranscriptView`, and tool cards. The remaining failure is architectural duplication: historical replay is normalized in the backend while live updates and tool presentation still re-infer tool identity, status, raw payloads, file changes, and grouping in the frontend. That split allows raw stream fragments and update-only events to leak as visible `unknown running...` cards.

The design should pull complexity downward into a single transcript projection boundary. Raw events remain available for debugging, but the page should primarily consume stable Mohist/coder turns and semantic assistant parts.

## Goals / Non-Goals

**Goals:**

- Render `/issue/:number/session/:sessionId` as a readable Mohist-to-coder transcript, not as an event log.
- Use one normalized transcript model for historical replay and live streaming convergence.
- Merge `tool_call` and `tool_call_update` into stable tool parts keyed by provider id, ACP id, or deterministic correlation fallback.
- Hide internal lifecycle events from the visible tool stream while preserving raw details behind explicit disclosure.
- Make context gathering, bash, edit/write/apply_patch, reasoning, errors, and file changes semantic conversation parts.
- Keep completed sessions understandable after refresh from persisted data.

**Non-Goals:**

- No composer, continue-conversation behavior, stop button, steering controls, or workflow dashboard redesign.
- No change to coder execution semantics, tool permissions, stage transitions, or pipeline control.
- No permanent removal of raw session data; raw prompt/input/output remains available for debugging.
- No new session-page paradigm beyond an opencode-style read-only transcript.

## Decisions

### D1: Treat `SessionTranscriptAssembler` As The Canonical Projection Boundary

`packages/cli/src/services/session-transcript-service.ts` should be the authoritative place that converts raw persisted stream events into `SessionTurn[]` and `SessionPart[]`. The frontend should not need to understand raw ACP event variants to decide what is a real tool, what is an update fragment, or what should be hidden.

The assembler will expose a slightly richer but still compact transcript shape:

- `SessionTurn.user` for Mohist prompts, including readable summary and raw prompt disclosure text.
- `TextPart` and `ReasoningPart` for coder prose and collapsed reasoning.
- `ToolPart` for one logical tool call, not one raw stream event.
- `ErrorPart` for timeout, cancellation, failure, and recovery messages.
- `FileChangeSummary[]` on file-changing tools and aggregated session metadata.
- `TranscriptWarning[]` for non-fatal normalization ambiguity.

This keeps the API interface deeper: one endpoint returns a usable transcript, while implementation complexity stays behind the service.

**Alternatives considered:** Normalize entirely in React. This would avoid backend refactoring but keeps the current problem: historical and live paths disagree, and UI components must know too much about event internals. Add a separate transcript-v2 endpoint. This avoids touching the existing response but creates two contracts for the same page; extending the existing session detail response is simpler.

### D2: Normalize Tool Events Through One Internal Pipeline

Replace separate start/update parsing assumptions with an internal normalized tool event structure before merging:

```ts
type NormalizedToolEvent = {
  kind: 'start' | 'update'
  toolCallId?: string
  correlationKey: string
  normalizedName: string
  sourceName?: string
  status: 'pending' | 'running' | 'completed' | 'failed' | 'cancelled'
  title?: string
  displayTitle: string
  target?: string
  rawInput?: string
  rawOutput?: string
  metadata?: Record<string, unknown>
  error?: string
  at: string
}
```

Normalization should flatten both top-level payloads and nested `toolCall` payloads, then infer fields in this order:

- Stable id from `toolCallId`, nested `toolCall.toolCallId`, `id`, `callId`, or provider correlation fields.
- Name from `toolName`, `name`, known title values, raw input shape, raw output metadata, then controlled `unknown` fallback.
- Target from file path, command, pattern/query, or title depending on tool class.
- Status from ACP status/state, with raw `started` mapped to `running` for display and terminal states preserved.

The logical tool map should be keyed by stable id when present. Without an id, use a per-turn pending queue keyed by normalized name plus target/title, not by tool name alone. This prevents a `tool_call_update` from becoming a separate unknown card when it is actually the completion of a pending tool.

**Alternatives considered:** Continue patching `parseToolCallStart()` and `parseToolCallUpdate()` separately. That preserves duplicated inference and makes update-only payloads easy to misclassify. Require upstream events to always provide perfect ids. That is desirable long-term but does not fix historical sessions or provider payload variance.

### D3: Hide Internal Events By Default And Model Them As Metadata

Only conversation-relevant events should produce visible parts: `mohist_prompt`, assistant text/reasoning chunks, logical tool calls, terminal errors, and file/change summaries. Step boundaries, lifecycle updates, raw ACP bookkeeping, and session maintenance events should not become `ToolPart`s.

If hidden events are useful for debugging, the API may count them or expose them through warnings/raw logs, but the transcript renderer must not display them inline as tools. This is the main protection against `unknown running...` caused by lifecycle fragments.

**Alternatives considered:** Render all unknown event types as generic tool cards. This maximizes visibility but directly violates transcript readability. Drop unknown event data entirely. That improves UI cleanliness but removes audit/debug value.

### D4: Keep Individual Tool Parts In The Transcript And Group Context Tools In The Renderer

The canonical transcript should retain individual `ToolPart`s in order. `SessionTranscriptView` should perform a small presentation pass that groups adjacent context tools into a synthetic visual group such as `Gathered context · 4 reads, 2 searches`.

Context tools include `read`, `read_file`, `glob`, `grep`, `search`, `list`, `membrowse`, `memread`, and `memsearch`. Grouping should not cross text, reasoning, error, file-change, bash, or edit/write/patch boundaries because those boundaries carry conversational meaning.

**Alternatives considered:** Emit grouped context parts from the backend. That makes this one UI simpler but hides exact sequence from other consumers and complicates live incremental updates. Never group context tools. That preserves detail but leaves the page feeling like a log viewer.

### D5: Render Tool Cards From Normalized `ToolPart`, Not Legacy `ToolCallEntry`

`ToolCallCard` should accept normalized transcript tool data directly or be wrapped by a thin adapter that loses no fields. The current adapter converts `ToolPart` back into `ToolCallEntry`, which forces the card to re-parse raw input and rediscover display type.

Rendering rules:

- Context group: count by normalized tool class, show representative targets, expand to individual inputs/outputs.
- Bash: title/description, command, status, duration, concise output preview, expandable full output.
- Edit/write/apply_patch: changed files first, additions/deletions when available, expandable raw patch/input/output.
- Unknown: use `displayTitle`, title, target, or source event label before falling back to `unknown`; raw payload remains collapsed.
- Running: show a restrained active state only for a logical tool whose latest status is non-terminal.

**Alternatives considered:** Keep `ToolCallEntry` as the rendering API. This minimizes edits but preserves a shallow, legacy interface that cannot express normalized status, warnings, category, and file summaries cleanly. Make one generic JSON disclosure for every tool. That is simpler but does not meet opencode-style readability.

### D6: Make Live Updates Converge To The Same Transcript Semantics

`useSessionTranscript()` may still apply SSE events optimistically, but it must use the same normalization rules and merge keys as historical replay. The hook should update an existing tool part by `toolCallId` or correlation key, rather than appending a new card for every `coder_tool_call` event.

On terminal session events, the hook should mark local UI as finalizing and invalidate the session-detail query. The refetched API transcript becomes canonical and replaces optimistic live state. This keeps live updates responsive without making SSE the permanent source of truth.

**Alternatives considered:** Stream fully assembled transcript parts from the backend. That would be the cleanest contract, but it requires a larger SSE change than this issue needs. Refetch after every SSE event. That guarantees canonical data but would be noisy and less responsive.

### D7: File Changes Are Conversation Output, Not A Separate Dashboard

File-changing tools should produce `changedFiles` summaries during transcript projection. The frontend should render those summaries inline after the relevant tool or as a compact turn-level result when multiple file-changing tools occur in one assistant turn.

The summary should include count, operation, path, and additions/deletions when available. Raw patch or edit payloads remain behind disclosure. Session metadata can aggregate these summaries for header-level context, but the primary presentation belongs in the transcript flow.

**Alternatives considered:** Compute file changes from current git diff. That can be useful elsewhere but is not reliable for historical transcript replay after rebases, follow-up edits, or cleanup. Show raw patches only. That preserves exact data but makes users parse implementation details before understanding what changed.

### D8: Preserve Backward Compatibility With Legacy Session Data

No database migration is required for this change. Existing `session_stream_log` and workflow-log fallback records should be reprojected best-effort. When prompts or tool ids are missing, the transcript should expose explicit incomplete/warning metadata rather than inventing certainty.

Legacy sessions without `mohist_prompt` keep the existing missing-prompt turn. Legacy no-id tools can still merge by correlation when payloads allow it; otherwise they should render once with the best available title and a warning.

**Alternatives considered:** Backfill historical session logs. That would improve old data but increases operational risk and is unnecessary for a read-only projection fix. Ignore legacy sessions. That would leave the refresh/replay acceptance criteria incomplete.

## Risks / Trade-offs

- [Risk] No-id tool correlation can pair the wrong update when multiple same-name tools run concurrently. -> Mitigation: key by normalized name plus target/title when available, keep a FIFO queue per key, and attach a warning when falling back to name-only matching.
- [Risk] Tool inference may misclassify provider-specific or future tools. -> Mitigation: use conservative inference, prefer explicit fields, keep raw data collapsed, and surface transcript warnings instead of hiding uncertainty.
- [Risk] Backend and live frontend normalization can still drift if implemented separately. -> Mitigation: keep live normalization intentionally small, use the same field names and merge keys, and always replace optimistic state with the canonical API transcript after refetch.
- [Risk] File additions/deletions from edit/write payloads are estimates. -> Mitigation: present them as tool-level summaries, not git truth, and keep exact raw patch/input behind disclosure.
- [Risk] Hiding lifecycle events could obscure useful debugging information. -> Mitigation: keep raw logs or hidden metadata accessible through explicit debugging disclosure, but do not render them as conversation parts.
- [Risk] The richer transcript response duplicates raw and normalized fields. -> Mitigation: keep additions optional and backward compatible, and prefer normalized fields for UI while preserving raw only for audit controls.

## Migration Plan

1. Extend transcript types to include normalized display status values, correlation warnings, optional raw event labels, and turn/session changed-file summaries while keeping existing fields compatible.
2. Refactor `SessionTranscriptAssembler` around a single tool normalization/merge pipeline and add internal-event filtering before visible part creation.
3. Improve tool id fallback to use per-turn pending queues keyed by normalized name plus target/title; remove name-only synthetic matching as the default path.
4. Update `GET /api/issues/:number/coder-sessions/:sessionId` to return the enriched canonical transcript and metadata without changing the route.
5. Update frontend shared types and `useSessionTranscript()` so live text/tool/error updates update existing parts and converge to the refetched canonical transcript after completion or recovery terminal events.
6. Refactor `SessionTranscriptView` and `ToolCallCard` to render normalized `ToolPart`s directly, including context groups, bash summaries, file-change cards, collapsed reasoning, and raw-data disclosures.
7. Add regression tests for pending/update merges, update-only events, no-id correlation, lifecycle event hiding, live duplicate prevention, context grouping, bash/edit/patch summaries, and refresh replay parity.
8. Rollback is a frontend/API projection rollback only: execution, persistence, and workflow state are unchanged. If rendering regresses, the route can fall back to the previous transcript display while keeping enriched API fields harmlessly unused.

## Open Questions

- Should the display status enum be renamed from current `started` to `running` throughout the transcript contract, or should the API retain `started` internally and map to `running` only in the UI?
- Should turn-level changed-file summaries be explicit `SessionPart` entries, or derived in `SessionTranscriptView` from file-changing tools in each turn?
- Which raw-debug entry point should expose hidden lifecycle events: an expandable section on the session page, existing workflow logs, or a separate developer-only raw transcript panel?
