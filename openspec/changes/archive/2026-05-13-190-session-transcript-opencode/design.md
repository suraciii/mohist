## Context

The current session transcript already has the main plumbing in place: the backend exposes normalized `turns`, the frontend projects those turns into `DisplayTurn`s, and `useSessionTranscript` appends live SSE updates. The remaining problem is architectural drift between those layers. Tool naming, content parsing, context grouping, running-state display, and ordering heuristics are split across `session-transcript-service.ts`, `useSessionTranscript.ts`, `session-transcript-display.ts`, `AssistantParts.tsx`, and `ToolCallCard.tsx`, so the page still leaks event-log behavior into the user-facing transcript.

Two constraints shape the implementation. First, `session_stream_log.created_at` is only second-precision today, so persisted reasoning/text/tool order cannot always be recovered exactly from timestamps alone. Second, we should not block transcript UX improvements on a storage migration; this change should improve readability immediately while keeping raw payloads and execution semantics unchanged.

## Goals / Non-Goals

**Goals:**

- Make the session page read as a Mohist-to-Coder transcript instead of a raw tool/event log.
- Introduce a single frontend `ToolRegistry` contract so visible tool identity and rendering do not depend on ad hoc conditionals or hardcoded whitelists.
- Reuse one normalization path for both replayed transcript parts and live SSE updates, so refresh and streaming converge on the same visible structure.
- Improve reasoning placement, tool summaries, context grouping, running-state rendering, diff visibility, copy affordances, and transcript metadata without removing audit access to raw data.
- Stage the work so P0 and P1 can ship with frontend-led improvements, while P2 backend timestamp fidelity remains optional but compatible.

**Non-Goals:**

- No redesign of agent execution, workflow stages, or SSE transport protocol beyond what transcript convergence requires.
- No requirement to fully clone opencode internals or data model field-for-field.
- No destructive migration of historical session data.
- No new editable transcript surface; the page remains read-only.

## Decisions

### D1: Keep Backend Transcript Parts Stable, Add A Frontend Projection Layer For Display Semantics

The backend remains responsible for returning normalized transcript parts (`text`, `reasoning`, `tool`, `error`) and session metadata. The frontend projection layer remains responsible for transcript-only presentation semantics: context grouping, reasoning folding, tool subtitles, copy controls, pacing, and synthetic display-only parts.

This keeps the API deep and stable while avoiding a backend contract that is too tightly coupled to one UI composition. The existing `projectSessionToDisplayTurns` path becomes the explicit projection boundary and will own synthetic UI parts such as `context-group` and any reasoning/text reorder heuristics used to compensate for second-level timestamps.

**Alternatives considered:** put all display grouping in the backend. Rejected because grouped/contextual display is UI-specific and would make live updates and future consumers harder to keep consistent.

### D2: Replace Whitelist Rendering With A `ToolRegistry`

Introduce a dedicated frontend registry module, likely under `packages/cli/web/src/components/session-transcript/tool-registry.tsx`, with one entry per known tool family. Each entry should expose:

- `matches(tool)` or key by normalized tool name
- `icon`
- `getTitle(tool)`
- `getSubtitle(tool)`
- `getBadges(tool)` or args metadata
- `renderContent(tool)`
- optional `group` or `category` hints such as `context`, `file-change`, `execution`, `question`, `network`

The registry becomes the only place that knows how `bash`, `read`, `grep`, `glob`, `webfetch`, `task`, `skill`, `question`, `apply_patch`, `edit`, `write`, and fallback tools should look. Unknown tools no longer map to a user-facing `unknown` label unless both the explicit tool name and the inference signals are absent. Otherwise the fallback title is the raw `toolName`, with subtitle extraction from `description`, `query`, `url`, `filePath`, `path`, or similar high-signal fields.

This pulls complexity downward: `AssistantParts.tsx` renders one `ToolPartView`, and the registry hides the parsing differences between tool families.

**Alternatives considered:** continue expanding `ToolCallCard.tsx` switch statements. Rejected because the current helper-based approach has already scattered knowledge across multiple files and still misses many tools.

### D3: Reuse Existing Tool Parsing Helpers As The First Registry Inputs, Then Centralize Them

`ToolCallCard.tsx` already contains `getToolLabel`, `getToolArgs`, `parseEditInput`, and `parsePatchOperations`. P0 should wire these helpers into the transcript tool views immediately so the visible output improves without waiting for a larger refactor. P1 should then move the durable helpers into a shared transcript utility module consumed by both the registry and live transcript hook.

This sequence minimizes risk:

- P0: use `getToolLabel` and `getToolArgs` in transcript rendering, add raw-name fallback, keep existing card structure.
- P1: extract durable parsing utilities out of `ToolCallCard.tsx`, build `ToolRegistry`, and migrate transcript components to it.

**Alternatives considered:** rewrite all parsing before any UX fixes land. Rejected because it delays the most visible problems even though reusable parsing logic already exists.

### D4: Treat Reasoning Ordering As A Frontend Compensation Problem First

The persisted transcript cannot reliably recover real interleaving when multiple parts share the same second. For this change, the frontend projection layer should improve perceived reasoning order by applying bounded semantic reordering within a turn:

- Keep original part order when timestamps are distinct.
- When a run of reasoning parts appears before the first text part within the same assistant response, attach that reasoning run to the nearest following visible assistant block instead of rendering a detached "thinking wall" at the top.
- Never move reasoning across prompt boundaries, errors, or tool groups.
- Keep reasoning collapsed by default.

This is intentionally conservative: it improves the reading flow without pretending to reconstruct perfect chronology. The design remains compatible with a later backend/storage upgrade to millisecond timestamps, at which point the reorder pass can become a no-op for newer sessions.

**Alternatives considered:** wait for database precision improvements first. Rejected because the user-visible transcript problem exists now and can be mitigated safely in the projection layer.

### D5: Context Grouping Should Be Count-Based And Type-Aware

`projectSessionToDisplayTurns` already groups adjacent context tools, but the summary is too narrow and the downstream rendering does not expose enough semantic value. The grouping pass should stay in the projection layer and classify tools by registry category plus normalized name. Group summaries should prefer counts by user intent:

- `X reads`
- `Y searches`
- `Z globs`
- optionally `memory lookups` or `web fetches` if those tools are grouped in future

Groups only form across adjacent context tools and must break on text, reasoning, non-context tools, or errors. A group with failures must surface failed state in the group header rather than hiding it.

**Alternatives considered:** show every read/grep card individually. Rejected because long context bursts dominate the transcript and obscure the actual response.

### D6: File-Change Tools Need Two Layers Of Output: Summary First, Raw Diff Second

For `apply_patch`, `edit`, and `write`, the transcript should render a first-class file-change result view before any raw JSON or patch text. The content pipeline is:

1. Parse tool payload into changed-file summaries.
2. When before/after text is available, render inline diff sections.
3. Keep raw patch/input/output available in expandable audit details.

The first implementation can use the current patch parsing helpers and estimated additions/deletions. A deeper diff viewer can follow without changing the registry contract because file-change tools already render through a dedicated content renderer.

**Alternatives considered:** continue showing raw patch JSON with a file list. Rejected because it makes the user reverse-engineer the result of the tool instead of seeing the result directly.

### D7: Live Transcript Updates Must Mutate The Same Logical Tool Shape Used By Replay

`useSessionTranscript` currently infers tool names, changed files, and merge correlation on its own. That logic should be reduced to event-to-normalized-tool adaptation, then reuse shared transcript utilities so live updates produce the same logical `ToolPart` fields the replayed transcript already uses.

The hook should continue to own transient client state:

- current live turns
- pending/running tool instances
- `isNearBottom`
- `newContentAvailable`
- finalizing/refetch triggers

But it should not own a parallel tool-display model. Terminal events still trigger invalidation/refetch so the backend transcript remains canonical after completion.

**Alternatives considered:** refetch on every live event. Rejected because it would sacrifice streaming responsiveness and create unnecessary API churn.

### D8: Auto-Scroll Should Be Follow-Mode Based, Not "Always Scroll On Update"

The transcript page should treat auto-scroll as an explicit follow mode controlled by reader position. The model is:

- If the reader is near bottom, new text/tool updates stay in follow mode and auto-scroll.
- If the reader scrolls away, follow mode pauses and new updates only set `newContentAvailable`.
- Nested scrollable regions such as code blocks, diff panes, and raw payload viewers should not accidentally re-enable or disable transcript follow mode.

P3 enhancements such as overflow anchoring can build on this model, but the current change only needs a reliable separation between transcript-scroll events and nested content-scroll events.

**Alternatives considered:** keep current simple bottom snapping. Rejected because it repeatedly interrupts reading during long or tool-heavy sessions.

### D9: Metadata Affordances Belong In The Session Header And Assistant Part Footer, Not In Separate Panels

Model name, duration, turn count, running/finalizing state, and copy actions should stay embedded in the transcript experience rather than introducing side dashboards. Session-wide metadata belongs in `SessionPage`/sticky header; assistant-response-local metadata and copy affordances belong near rendered text parts.

This preserves the transcript-first reading model and avoids turning the page back into a control surface.

**Alternatives considered:** add a separate metadata sidebar. Rejected because it splits attention and works against the proposal's goal of a clean transcript surface.

## Risks / Trade-offs

- [Risk] Frontend semantic reordering of reasoning could misrepresent exact chronology in edge cases. → Mitigation: keep the reorder heuristic narrow, local to a turn, and removable once millisecond timestamps are available.
- [Risk] ToolRegistry may duplicate some backend inference logic. → Mitigation: keep identity normalization on the backend/live hook, and limit the registry to display concerns plus lightweight subtitle extraction.
- [Risk] Shared parsing extraction from `ToolCallCard.tsx` could create churn across existing components. → Mitigation: phase it in by reusing helpers first, then extracting only the durable parsing functions.
- [Risk] Diff rendering may be incomplete for tools that only expose patches or only expose replacement text. → Mitigation: always show a file summary, render richer before/after views when data exists, and preserve raw payload fallback.
- [Risk] New live-follow behavior could regress current scrolling if nested scroll containers are not handled carefully. → Mitigation: keep transcript scroll detection at the page container and mark nested scrollable regions explicitly.
- [Risk] Empty `specs/` under this change means design detail could outrun formal requirement text. → Mitigation: keep this design aligned to the already approved capability requirements in `agent-session-ui` and `pipeline-session-events`, and let delta specs codify the user-visible contract next.

## Migration Plan

1. Implement P0 in the current transcript components: raw tool name fallback, `getToolLabel`/`getToolArgs` wiring, and closed-by-default reasoning.
2. Extract shared transcript parsing helpers from `ToolCallCard.tsx` into a transcript utility module without changing payload semantics.
3. Add frontend `ToolRegistry` and migrate tool rendering in `AssistantParts.tsx` and related transcript components to registry-driven views.
4. Expand the projection layer in `session-transcript-display.ts` to improve reasoning placement, richer context grouping, and synthetic display-only transcript parts.
5. Refactor `useSessionTranscript.ts` to consume the shared parsing/normalization helpers so live tool updates and replayed transcript parts converge.
6. Add file-change summary and diff renderers, then layer in copy controls, header metadata, and running-state animations.
7. Verify with component/hook tests and transcript fixtures covering unknown tools, grouped context, reasoning ordering, live/refetch parity, diff rendering, and paused auto-scroll.
8. Optionally follow with a backend migration to millisecond timestamp fidelity in `session_stream_log`; this is additive and does not block rollout.
9. Rollback path: keep the enriched parsing utilities and backend transcript shape, but revert transcript components to simpler rendering if the new registry-based UI causes regressions.

## Open Questions

- Should `question`, `task`, and `skill` tools be grouped under one broader "agent coordination" category visually, or should each keep a distinct renderer from the start?
- For the first diff viewer, is side-by-side comparison necessary, or is a compact unified diff with before/after snippets sufficient for the intended troubleshooting use case?
- When millisecond timestamps are added later, should the frontend continue applying the reasoning reorder heuristic for legacy sessions only, or for all sessions behind a confidence check?
