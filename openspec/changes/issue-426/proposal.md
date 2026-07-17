## Why

The session transcript's core invariant is to faithfully reflect what actually happened in a session: every tool call has a recognizable name and target, assistant text preserves the agent's original paragraph structure, activity indicators only appear while work is live, and every control is reachable by assistive tech. On the session page (e.g. task session `T-001.1`, model MiniMax-M3) all four deviate at once — tool calls titled literally "unknown", paragraphs fused into "…usage:Let me…", a "Streaming…" indicator lingering on a session that ended hours ago, and icon-only controls exposed to screen readers as "unknown".

This is the minimal correctness fix that makes the transcript trustworthy again before #427 rewrites the transcript's visual form. It is intentionally scoped to the presentation layer and client-side state derivation only — no data model or event-protocol change. The liveness gate it establishes is also a prerequisite #428 consumes, so it must land first and exclusively here.

## What Changes

- **Tool-name resolution never yields "unknown".** When a tool name is missing, derive a recognizable, human-readable title from the tool's input (command, path, search/query string, etc.). The literal string "unknown" is no longer a valid display title anywhere in the transcript.
- **Streaming text preserves paragraph boundaries.** Concatenating streamed assistant deltas no longer fuses adjacent paragraphs/sentences; the accumulated text matches the agent's original output structure.
- **Activity indicators are liveness-gated.** Streaming/Thinking indicators render only while the session is alive; an ended/Finalizing session never shows them. A live session continues to render them normally. **This change exclusively owns the liveness gate** — #428 consumes the corrected state and does not re-fix it.
- **Transcript controls are accessible.** Every icon-only / expand-collapse control in the transcript exposes a readable accessible name, its expanded/collapsed state, and decorative icons are hidden from assistive tech.
- **No structural redesign.** No change to the transcript data model, event protocol, tool-display component structure (that is #427's job), real-time duration/progress sense (#428), or the event collection/reflow pipeline.

Non-goals (per issue): no transcript visual-form redesign; no progress/duration sense; no event-collection/reflow changes (if a "unknown" gap roots in the collection side, record evidence and open a separate issue rather than touching that pipeline here); no new transcript data-model fields.

## Capabilities

- `transcript-tool-naming`: How a tool call's display title is derived in the transcript. Requires that the derivation always produces a recognizable, human-readable title from the tool name, the call title, and raw input fields (command, file path, search/query, etc.), and never renders the literal "unknown". Covers the inference/fallback chain in the transcript tool utilities and the display-label resolution feeding the tool views. Establishes the boundary that a gap rooted in event collection is escalated to a separate issue, not patched in display.
- `transcript-text-fidelity`: How streamed assistant text is accumulated into transcript parts so that the agent's original paragraph structure is preserved. Requires that concatenating deltas and closing/reopening text parts never fuses adjacent paragraphs or sentences, and the rendered text matches the source output.
- `transcript-activity-indicators`: When streaming/thinking activity indicators may render. Requires they are gated on session liveness — only an alive (running) session renders them; any ended/Finalizing/inactive session renders none, while a live session renders them normally. Covers the indicator components and the client-side state (streaming/thinking flags) plus the liveness signal that gates them. This capability owns the liveness gate that #428 depends on.
- `transcript-control-accessibility`: Accessible names and disclosure semantics for transcript interaction controls (icon-only buttons, expand/collapse tool and diff controls). Requires every such control to expose a readable accessible name and expanded/collapsed state, decorative icons to be hidden from assistive tech, and streamed content to be announced appropriately.

## Impact

- **Web (`packages/web`)** — all changes are in the `session-transcript` widget and its session-page wiring; production path only:
  - Tool naming: `widgets/session-transcript/model/transcript-tool-utils.ts` (`inferToolName`/`normalizeToolName`, the `'unknown'` fallback), `widgets/session-transcript/ui/tool-views/shared.tsx` (`getToolDisplayLabel`), `widgets/session-transcript/ui/tool-registry.tsx` (`FallbackEntry.getTitle`).
  - Text fidelity: `widgets/session-transcript/model/transcript-state.ts` (`appendTextToTurn`, `appendReasoningToTurn`, `closeActiveTextPart`), `widgets/session-transcript/model/useSessionTranscript.ts` (delta/reasoning event handlers).
  - Activity indicators: `widgets/session-transcript/ui/SessionTranscriptLayout.tsx` (`StreamingIndicator`/`ThinkingPlaceholder`, render gating at line 91-92), `widgets/session-transcript/model/useSessionTranscript.ts` (`isStreaming`/`isThinking` flags and debounce), via the liveness signal already plumbed through `SessionDataSourceResult` (`pages/session/data/SessionDataSource.ts`) and `pages/session/ui/SessionDetailShell.tsx`.
  - Accessibility: `widgets/session-transcript/ui/tool-views/index.tsx` (`ToolRowView`, `ContextGroupView`), `widgets/session-transcript/ui/TurnList.tsx` (`TurnDiffs`), `widgets/session-transcript/ui/tool-views/shared.tsx` (decorative `ToolIcon`/`ToolStatusDot`), `widgets/session-transcript/ui/SessionTranscriptLayout.tsx` (indicator live-region semantics).
  - Note: the legacy `SessionTranscriptView.tsx`/`ToolCallCard.tsx` renderers carry duplicate "unknown"/a11y logic but are **not exported** (`widgets/session-transcript/index.ts`) and are test-only — out of scope (destined for #427 removal); a fix there is not required.
- **Server / Runner / CLI**: none.
- **Dependencies**: none added.
- **APIs**: none — no data-model or event-protocol change.
- **Tests (`packages/web`)**: extend existing transcript model specs (tool-name inference, append/concatenation state) and component specs (indicator gating, control a11y) per `design/testing.md`; no real time/external deps.
- **Risk (low)**: presentation layer + client state derivation only, single subsystem, no API or data change; mitigated by preserving all existing `data-testid` anchors and leaving the tool-display component structure for #427.
