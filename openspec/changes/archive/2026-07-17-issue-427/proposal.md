## Why

Reading a long Mohist session today means scrolling through a chat UI: assistant text is squeezed into a width-limited bubble, the user prompt is a right-aligned rounded bubble, and every one of dozens of tool calls is a bordered box. To follow what an agent did you scan an endless wall of cards. Coding-agent transcripts (codex / claude-code / opencode) read the opposite way — a single tight column where the eye travels straight down, each action is one line, and detail is revealed on demand. #426 has already done the minimal correctness fixes to transcript data; this issue reshapes that data into that scannable timeline so reading a session stops being exhausting. It is the structural foundation that #428 (live activity) and #429 (navigation) mount onto, which is why it must land before them and why each tool row needs a stable, locatable structure.

## What Changes

- **The transcript becomes a flat single-column timeline.** All content shares one left margin and fills the column width; the `max-w-2xl`/`max-w-[80%]` width caps on turns, assistant text, and prompts are removed so the reading line is straight and vertical.
- **Each turn gets a full-width divider bar** showing the turn number, prompt type, start time, and (when available) turn duration — replacing the current small right-aligned "Turn N · time" label.
- **The user prompt renders as a collapsed blockquote-style block, not a chat bubble.** Long prompts are collapsed by default with an expand affordance; the rounded bubble styling is removed.
- **Every tool call defaults to one line**: a status symbol, a verb-led title (read / ran / edited <file>), key parameters, and duration. Typed detail (terminal output, inline diff, result summary) is hidden until the row is expanded.
- **Edit-type tool calls show their changed file and +N/−M line stats inline on the row**, not only behind an expand.
- **Failed tool calls render their whole row red** so failures are visible while scanning, not buried inside an expanded card.
- **Running tool calls render as a one-line in-progress state** (e.g. "Editing X…") within the same row structure — the live-ticking duration and current-activity bottom bar stay out of scope (#428).
- **Consecutive exploratory tool calls (read / grep / glob / search) collapse into a single expandable grouped row** summarizing the batch (e.g. "5 reads · 3 searches"), rather than one card per call.
- No change to the transcript data model, event protocol, session header/framework layer, or collection pipeline.

## Capabilities

- `session-transcript-timeline`: The flat single-column transcript container and turn-level structure — full-width unified left margin with no bubble width-capping, a full-width turn divider bar (turn number, prompt type, start time, turn duration), and the prompt rendered as a collapsed-by-default block rather than a chat bubble. Covers the "reading line is one straight vertical column" invariant.
- `transcript-tool-rows`: Per-tool-call rendering as a default-collapsed single row — status symbol, verb-led title, key parameters, and duration on one line; click to expand typed content (terminal/command output, inline diff, result summary). Covers edit tools showing changed file + +N/−M inline, failed calls rendering the whole row red, and running calls rendering a one-line in-progress state. Each row exposes a stable, locatable structure for #428/#429 to mount on.
- `transcript-context-grouping`: Merging consecutive exploratory tool calls (read / grep / glob / search) into one expandable grouped row that summarizes the batch, with the individual calls available on expand.

## Impact

- **Web (`packages/web/src/widgets/session-transcript/`)** — primary rewrite surface:
  - `ui/SessionTranscriptLayout.tsx` — drop the `lg:max-w-4xl` centered grid; the timeline becomes full-width single-column.
  - `ui/TurnList.tsx` — remove `max-w-2xl mx-auto`; replace the right-aligned `TurnHeader` with a full-width turn divider bar carrying prompt type + duration; rework `TurnItem`/`TurnDiffs` into the line form.
  - `ui/PromptBlock.tsx` — replace the `rounded-2xl` right-aligned bubble with a full-width collapsed block.
  - `ui/AssistantParts.tsx` — remove the `max-w-[80%]` cap on `AssistantTextPartView` so assistant markdown fills the column.
  - `ui/tool-views/index.tsx` — rework `ToolRowView` (one-line default with status symbol/verb-title/params/duration, inline edit +N/−M, whole-row red on failure) and `ContextGroupView` (collapsed group line).
  - `ui/ToolCallCard.tsx` and the typed content views (`tool-views/bash-view.tsx`, `diff-view.tsx`, `read-view.tsx`, `search-view.tsx`, etc.) — aligned to the expand-on-demand row model.
  - `model/session-transcript-display.ts` — the `DisplayTurn`/`DisplayAssistantPart` projection is expected to be reused as-is; any new needs are limited to row-title/duration derivation helpers.
- **Web (`packages/web/src/pages/session/ui/`)** — `SessionDetailShell.tsx` hosts `SessionTranscriptLayout` unchanged in contract; only its child's rendering changes.
- **Tests** — the widget has extensive render specs (`ui/*.test.tsx`, incl. tool-labels, file-tools, context-tools, todo-tools, raw-tool-payload, turn-file-changes, accessibility) that assert the current bubble/card DOM; these regress broadly and must be migrated to assert the timeline/row DOM. New coverage for full-width invariant, divider bar content, edit inline +N/−M, whole-row red, and context-group collapsed summary.
- **Data / events / protocol**: none. Purely presentation-layer.
- **Dependencies**: none.
- **Risk**: medium — large-area rewrite of the transcript rendering components with regression surface across every tool-type render path; mitigated by the data model being unchanged and by migrating the existing specs in lockstep.
