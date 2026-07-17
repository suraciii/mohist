## Context

The session transcript widget (`packages/web/src/widgets/session-transcript`) renders an agent session as a list of turns. Its core invariant is to faithfully reflect what happened: recognizable tool names, the agent's original text paragraph structure, liveness-accurate activity indicators, and accessible controls. Issue #426 reports four simultaneous fidelity breaks observed on a task session (`T-001.1`, MiniMax-M3): tool titles rendered as the literal `"unknown"`, assistant paragraphs fused (`"…usage:Let me…"`), a `"Streaming…"` indicator lingering on a session that ended hours ago, and icon-only controls exposed to screen readers as `"unknown"`.

This is the **minimal correctness fix** before #427 rewrites the transcript's visual form, and it establishes the **liveness gate** that #428 consumes. Scope is strictly the Web presentation layer and its client-side state derivation — no data model, event protocol, or collection/reflow change.

**Two renderers exist; only one is production.** `widgets/session-transcript/index.ts` exports only `SessionTranscriptLayout` (+ `useSessionTranscript`, `projectTurn`). The legacy `SessionTranscriptView.tsx` / `ToolCallCard.tsx` duplicate the "unknown"/a11y logic but are **not exported** and are test-only. This change targets the production path; the legacy files are out of scope (#427 removes them).

The liveness signal (`isRunning`) already flows end-to-end: `useGenericSessionDataSource`/`useIssueSessionDataSource` derive it from the session summary and expose it through `SessionDataSourceResult`; `SessionTranscriptLayout` already receives it as a prop (used today only for the empty state).

## Goals / Non-Goals

**Goals:**
- Tool-call display title is never the literal `"unknown"`; it is inferred from the call title and raw input, with a generic last-resort label.
- Streamed assistant text preserves the agent's original paragraph boundaries (no cross-paragraph fusion).
- Streaming/thinking activity indicators render only while the session is alive (`isRunning`); an ended session renders none.
- Transcript interaction controls expose readable accessible names, disclosure state (`aria-expanded`), hidden decorative icons, and live-region status.

**Non-Goals:**
- No transcript visual-form redesign, tool-display component restructuring (#427).
- No real-time duration/progress sense (#428).
- No event collection/reflow pipeline change; if a gap roots there, record evidence and escalate to a separate issue (do not patch the collection side here).
- No transcript data-model or event-protocol field additions.
- No change to the legacy test-only renderer.

## Decisions

### D1. Tool naming: centralize "never unknown" at display-title resolution

The `"unknown"` title has three birth sites that converge on one display path:
1. `inferToolName` (transcript-tool-utils.ts:382) falls back to `toolName ?? 'unknown'`; `normalizeToolName` (line 417) re-emits `'unknown'`. This becomes `normalizedName`.
2. `inferDisplayTitle` (line 442) returns `DISPLAY_TITLES[normalized] ?? toolName` → `"unknown"` when `normalized` is `unknown`.
3. `FallbackEntry.getTitle` (tool-registry.tsx:29) returns `getToolLabel(toolName, rawInput) ?? toolName` → `"unknown"` when `getToolLabel`'s default-case extraction (url/path/desc/query) finds nothing.
4. `updateToolInTurn` (transcript-tool-state.ts:250) seeds `toolName: updates.toolName ?? 'unknown'` when minting a new tool part.

`inferToolName` already does substantial semantic inference (`inferSemanticToolName`/`inferTitleToolFamily` sniff command, path, query, url, patch text, delegation/skill markers). The remaining gap is the last-resort return.

**Decision:** keep `normalizedName === 'unknown'` as an internal registry key (changing it would ripple through correlation/display-field logic and risk #427), but make the **user-visible display title** never `"unknown"`:
- `FallbackEntry.getTitle` returns a generic descriptive label (e.g. `"Tool call"`) instead of `toolName` when `toolName`/normalized is `unknown` and no input content was extractable.
- `inferDisplayTitle` returns the same generic label instead of `?? toolName` for the `unknown` case.
- `getToolDisplayLabel` (shared.tsx:11) is the single choke point feeding `ToolRowView`/`ContextGroupView` labels — the floor is enforced here so every consumer is covered.

The internal semantic inference stays as-is; we do **not** weaken it. The change is purely the last-resort floor + ensuring the floor propagates through `inferDisplayTitle`.

**Alternatives considered:**
- *Replace `'unknown'` at the `normalizedName` source.* Rejected: `normalizedName` drives registry lookup, correlation keys (`getCorrelationKey`), and context-grouping; rewriting it spreads the change across model and UI and conflicts with #427's rewrite. The display title is the user-facing contract, so gate there.
- *Broaden `getToolLabel` to never return undefined.* Rejected as insufficient alone: `inferDisplayTitle` and `FallbackEntry` both have independent `?? toolName` floors. Centralizing at `getToolDisplayLabel` + the two title producers covers all paths.

### D2. Text fidelity: localize-then-fix, with the web contract pinned by a regression test

Code reading establishes the web concatenation is **already lossless**: `appendTextToTurn` (transcript-state.ts:126) does `existing.text + text` with no whitespace stripping — confirmed by grep (the only `\n` manipulation in the widget is patch-text unescaping in `transcript-tool-utils.ts:166`, unrelated to assistant text). For loaded sessions, `initialTurns` come straight from `transcriptResponse.turns` (`useGenericSessionDataSource.ts:75`); the web does **not** reconstruct text from chunks. `TranscriptMarkdown` is stock `react-markdown` + `remark-gfm`, which renders `\n\n` as separate `<p>` blocks.

So the `"usage:Let me"` fusion cannot originate in lossy web concatenation. The candidate sites are: (a) the live path where interleaved `message.delta`/`coder_text_chunk` events append to one open text part without a boundary, or (b) upstream — deltas that omit the inter-paragraph separator, or server-built persisted text parts that joined chunks without separators.

**Decision:** follow the issue's own escalation clause:
1. Add a regression test (unit on `appendTextToTurn` + a `TranscriptMarkdown` render assertion) that feeds a multi-paragraph delta sequence — including the dual `message.delta`/`coder_text_chunk` names and the reasoning-interrupt-then-resume case — and asserts (a) the accumulated text equals the deltas concatenated in order, and (b) `\n\n` boundaries present in the stream render as distinct paragraphs. This pins the web presentation contract regardless of root cause.
2. Localize the actual symptom against that test. If it reproduces in the web live path with deltas that carry `\n\n`, fix the web append/render path. If it reproduces only with deltas that omit separators or only in persisted turns, the gap is upstream (collection/reflow): record evidence and open a follow-up issue; do **not** modify the collection pipeline in this change.

The web presentation guarantee (lossless concatenation + correct paragraph rendering) is the deliverable for this issue; the upstream gap, if confirmed, is explicitly escalated per the non-goals.

**Alternatives considered:**
- *Insert a separator between every delta.* Rejected: deltas are token/chunk-level (the event shape is a bare `text` field); blindly inserting `\n\n` would corrupt intra-word and intra-paragraph splits.
- *Assume the bug and rewrite concatenation.* Rejected: the code is verified lossless; a speculative rewrite risks regressions and violates the minimal-fix scope.

### D3. Activity indicators: authoritative liveness gate at the render site + flag hygiene

Root cause: `SessionTranscriptLayout.tsx:91-92` gates the indicators on `isStreaming`/`isThinking` **only**. `isStreaming` is a 2s debounce (`bumpTranscriptVersion`, useSessionTranscript.ts:101) that is not tied to `isRunning`; on event replay of an already-ended session it can be bumped true again, and the 2s timer (a `setTimeout`) is exactly the kind of time-based logic the testing rules require to be controllable.

**Decision:** the render site is the authoritative gate:
- `{isStreaming && isRunning && <StreamingIndicator/>}`
- `{isThinking && isRunning && turns.length > 0 && <ThinkingPlaceholder/>}`

This is minimal, additive (it can only *suppress* an indicator on a non-running session, never remove one from a truly-live session), and leaves the flag semantics untouched so #428 can still consume `isStreaming`/`isThinking`.

**Defense in depth (hook hygiene):** additionally clear `isStreaming`/`isThinking` when `isRunning` transitions to false. The reset effect (line 137) and the derived-thinking effect (line 151) already do this on `isRunning` change, but `isStreaming`'s debounce is independent — extend the `isRunning` effect to also `clearStreaming()`. This prevents stale flags leaking to other consumers (#428).

**Per-part streaming glyph** (`assistant-text-streaming-glyph`, AssistantParts.tsx:32) is gated on `isIncomplete || isStreaming`. Propagate liveness so it renders only while `isRunning` (the glyph should never show on a completed part of an ended session). This requires threading `isRunning` (or a liveness-aware `showStreamingGlyph`) into `AssistantParts`/`AssistantTextPartView`.

**Alternatives considered:**
- *Gate only in the hook (clear flags on end), not at render.* Rejected as the sole fix: the render site is the single source of truth for what the user sees, and flag-clearing races (debounce timer vs. event ordering) can still leak. The render gate is robust to any flag staleness; hook hygiene is additive safety.
- *Drive indicators off `statusKind === 'live'` instead of `isRunning`.* Rejected: `statusKind` is a *display* derivation (incl. wall-clock staleness in `getSessionStatusKind`); `isRunning` is the authoritative liveness boolean already plumbed to the layout. Mixing in `statusKind` would couple indicator visibility to the staleness clock.

### D4. Accessibility: disclosure semantics + hidden decorative icons + status live region

Current gaps in the production renderer: `ToolRowView` (tool-views/index.tsx:132), `ContextGroupView` (line 210), and `TurnDiffs` (TurnList.tsx:108) toggle `expanded` state but expose **no `aria-expanded`**; their chevrons, `ToolIcon`, and `ToolStatusDot`/`RunningIndicator` (shared.tsx) are decorative but **not `aria-hidden`**. The `TurnList` root uses `role="log"` (which per ARIA implies `aria-live=polite`, so streamed content is already announced), but the indicators render *outside* that region and are not announced.

**Decision:**
- Add `aria-expanded={expanded}` to each disclosure control. For `ToolRowView`, set it only when `showExpandableDetails` is true (otherwise the button is non-disclosing, `onClick` undefined); `ContextGroupView` and `TurnDiffs` are always disclosing.
- Mark all decorative SVGs `aria-hidden="true"`: `ToolIcon`, `ToolStatusDot`/`RunningIndicator`, and the chevron SVGs in all three controls. (Pair with `focusable={false}` is unnecessary; `aria-hidden` suffices and the SVGs carry no role.)
- Accessible names come from the existing visible text (`{toolLabel}`, context-group title prefix, `"N files changed"`). Do **not** add redundant `aria-label`s; instead rely on the naming fix (D1) so no name is `"unknown"`. The `<details>`/`<summary>` reasoning blocks (AssistantParts.tsx:60) already have native semantics — no change.
- Give `StreamingIndicator`/`ThinkingPlaceholder` `role="status"` (implies `aria-live=polite`) so their appearance/removal are announced. Keep `TurnList`'s `role="log"` (already a polite live region) for streamed content.

**Alternatives considered:**
- *Add explicit `aria-live="polite"` to `TurnList`.* Rejected as redundant: `role="log"` already implies it; adding it is harmless but noise. (If a future AT-compat issue arises, revisit.)
- *Wrap each control's icon in an `aria-hidden` span.* Rejected: marking the SVG itself is sufficient and less markup.

## Risks / Trade-offs

- [Text-fidelity root cause is upstream (collection/reflow)] → Mitigation: the regression test pins the web contract; if localization confirms upstream, record evidence and open a follow-up issue per the non-goals rather than touching the pipeline. Risk: the acceptance criterion ("no fusion") may require the follow-up to fully close; this issue delivers the web-correct path + the escalation.
- [Liveness gate suppresses a legitimately-live indicator if `isRunning` is mis-derived] → Mitigation: the gate is strictly additive — it only suppresses when `isRunning` is false. `isRunning` is the existing authoritative signal used elsewhere; a mis-derivation would be a pre-existing, broader bug.
- [Generic last-resort tool label could mask a real upstream collection gap] → Mitigation: the spec requires the gap be escalated; the generic label is a readable floor, not a substitute for missing data.
- [Legacy test-only renderer retains `"unknown"`/a11y gaps] → Mitigation: explicitly out of scope; it is unexported and slated for #427 removal. Note in case reviewers grep for `"unknown"`.
- [`aria-expanded` on a button that is conditionally non-disclosing] → Mitigation: set the attribute only when the control actually discloses (`showExpandableDetails`).

## Migration Plan

Web-only change; no data migration, no API or protocol change, no new dependencies.

- **Deploy:** normal Web build (`npm run build`); ships in the next frontend release.
- **Verify:** `npm run typecheck -w packages/web`; `npm run test:run -w packages/web`. New tests: tool-name inference/last-resort (unit, collocated), `appendTextToTurn` + paragraph render (unit), indicator liveness gating (spec, `tests/`), control a11y attributes (render, collocated). No browser test required (per `design/testing.md`, browser tests are separate and not in default `npm test`).
- **Rollback:** revert the commit; no persistent state or schema affected.

## Open Questions

- **Text-fidelity layer (web vs upstream):** resolved by the localization test (D2 step 2). If upstream, the follow-up issue owns the remaining fix; this issue delivers the web-correct contract + evidence.
- **Exact last-resort label wording** (e.g. `"Tool call"` vs `"Tool"`): minor; decide during implementation, the contract is only "human-readable, never `unknown`".
- **Whether to surface the per-part streaming glyph's liveness via a single derived prop** vs threading `isRunning` into `AssistantParts`: implementation detail; prefer the smaller surface (a derived `showStreamingGlyph` boolean) to avoid widening `AssistantParts`' props ahead of #427.
