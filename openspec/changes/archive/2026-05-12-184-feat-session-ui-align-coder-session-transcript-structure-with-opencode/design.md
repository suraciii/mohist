## Context

Mohist already has a dedicated coder session page and a transcript service, but the current structure still reflects its event-log origin. `SessionPage.tsx` and `SessionTranscriptView.tsx` render a readable transcript, yet the composition is still card-heavy: prompt bubbles are visually chat-like, assistant output is split from tool evidence, context gathering appears as repeated tool rows, and patch/edit/write output is primarily surfaced as tool details instead of as part of the turn result. `useSessionTranscript.ts` also performs live-time inference that does not fully match replay-time transcript assembly in `session-transcript-service.ts`, which creates visible divergence between running and refreshed sessions.

The change should reuse the existing API and persistence model where possible. A full opencode `MessageV2` migration, pagination system, or global sync reducer is out of scope for this iteration, so the implementation needs a display-oriented layer that can project today's `CoderSessionDetail.turns` into an opencode-like read-only transcript without requiring a storage redesign. The capability specs for this change focus on transcript-first UI structure, API normalization, stable session tracking, and live-event convergence rather than a storage migration.

The main constraint is fidelity: the page should feel like an opencode session transcript, not like a Mohist admin screen with nicer cards. That means the implementation must prefer turn readability, stable ordering, and low-noise defaults over exposing every raw event.

## Goals / Non-Goals

**Goals:**

- Introduce a display contract that converts current session turns into prompt-led transcript turns with ordered assistant parts.
- Make replay and live updates converge on the same visible structure, statuses, tool grouping, and changed-file sections.
- Replace heavy tool cards with compact opencode-like tool parts, including grouped context gathering and muted reasoning.
- Move patch/edit/write/diff output into the natural end of the assistant turn, with file-level summaries and expandable detail.
- Rebuild the session page layout around a centered transcript column, sticky in-timeline title, subtle live status, and user-respecting auto-follow.

**Non-Goals:**

- Redesign session persistence into full opencode `MessageV2` storage.
- Add workflow summary dashboards, artifact dashboards, composer input, follow-up actions, or terminal side panels.
- Implement cursor pagination, session list caching, or the full opencode global sync architecture.
- Expose raw event JSON or internal tool chatter as the default primary UI.

## Decisions

### D1: Add a frontend display adapter between API transcript data and rendering

The page will stop rendering `SessionTurn.assistant` directly. Instead, the frontend will introduce a display adapter that maps API transcript turns into a render-only structure such as:

```ts
type DisplayTurn = {
  id: string
  startedAt: string
  completedAt: string | null
  prompt: DisplayPrompt
  assistantParts: DisplayAssistantPart[]
  changedFiles: DisplayChangedFile[]
  state: 'idle' | 'streaming' | 'finalizing' | 'error'
}
```

The adapter is responsible for transcript semantics, not just formatting. It will:

- keep each `mohist_prompt` turn as the stable visible boundary
- project prompt summaries into compact user-message-like blocks while preserving raw prompt text for expansion
- collapse repeated lifecycle fragments into one logical tool part
- hide `todowrite` and stale `unknown` placeholders by default
- group consecutive context tools into a `ContextToolGroupPart`
- promote patch/edit/write metadata into file-level changed-file output for turn endings
- insert subtle divider or status parts for interruption, recovery, and cancellation events

This keeps rendering components simple and allows the live hook and replay payload to share one projection path.

**Alternatives considered:**

- Render directly from existing `SessionTurn` parts and add conditionals in JSX. Rejected because it would spread transcript semantics across `SessionTranscriptView`, tool components, and live update hooks, making replay/live divergence worse.
- Redesign the API to emit final display-ready UI models immediately. Deferred because this iteration should minimize backend churn and preserve compatibility with the current session endpoint.

### D2: Strengthen backend transcript normalization, but stop short of a storage redesign

`session-transcript-service.ts` will remain the canonical replay assembler, but it should emit stronger normalized tool data so the frontend no longer has to guess core lifecycle state. The service should:

- merge `tool_call` and `tool_call_update` events by `toolCallId` into one logical tool record
- suppress placeholder records that are still `unknown`, have no usable input/output, and never become user-visible tools
- normalize tool status to a stable set: `pending`, `running`, `completed`, `error`, `cancelled`
- unwrap tool metadata needed for display: title, subtitle target, relative path, changed file list, patch stats, and warnings
- emit deterministic ordering so replay after refresh matches the same visible order used during live streaming
- mark hidden/internal tools explicitly when possible so the frontend does not maintain a growing denylist

Live updates in `useSessionTranscript.ts` should consume the same normalization helpers or equivalent shared logic so optimistic rendering follows replay behavior instead of re-inferencing everything independently.

**Alternatives considered:**

- Keep all normalization in the frontend. Rejected because replay and live paths are already split, and frontend-only inference is the main reason `unknown` placeholders and lifecycle duplication leak into the UI.
- Move immediately to an opencode-style persisted message schema. Rejected for scope; it would broaden the change from transcript presentation into storage migration.

### D3: Recompose the page into transcript-native layout primitives

The page should be restructured around transcript reading rather than dashboard chrome. The implementation will split `SessionPage.tsx` into layout primitives roughly aligned with the target structure:

- `SessionTranscriptLayout` or equivalent page shell with centered content column
- `StickySessionTitle` rendered inside the timeline flow, not as a separate app-header block
- `TurnList` with `role="log"`
- `SessionTurnView` for each prompt-led turn
- `TurnDiffs` as the final section within a turn
- `JumpToBottom` compact floating control

The centered column should use a wider reading width on desktop and a narrower mobile-safe width, replacing the current full-width dashboard feel. Long turns should opt into `content-visibility: auto` or an equivalent containment strategy to keep large transcripts responsive.

This decomposition makes the transcript structure explicit and prevents unrelated issue/session metadata from dominating the reading flow.

**Alternatives considered:**

- Keep the current page component and progressively tweak styles. Rejected because the existing composition bakes in a top header, card sections, and dashboard spacing that fight the desired transcript-first layout.
- Collapse everything into one large `SessionTranscriptView`. Rejected because the page needs separate responsibilities for sticky title, log semantics, turn rendering, and follow/jump behavior.

### D4: Render assistant output as ordered parts, not as separate text and tool zones

Within each turn, assistant content should be rendered as one ordered part stream. The display adapter should support at least these renderable part kinds:

- `text`
- `reasoning`
- `thinking`
- `tool`
- `context-group`
- `error`
- `divider`

The renderer will no longer show a `Coder` heading followed by a stack of independent cards. Instead, text, reasoning, tool evidence, and error or recovery markers appear in sequence, matching how the assistant actually progressed through the turn.

Reasoning should use muted markdown or collapsible text, with an empty active reasoning stream represented as a shimmer thinking placeholder rather than a debug dump. Text parts remain markdown-first. If the turn is live and no visible content exists yet, the renderer shows the thinking state rather than a blank log section.

**Alternatives considered:**

- Continue rendering separate regions for assistant text, thought text, and tools. Rejected because it preserves the workflow-log mental model and makes tool evidence feel detached from the assistant response.
- Hide reasoning entirely. Rejected because muted reasoning is part of the desired opencode feel and helps users understand progress without flooding the page.

### D5: Replace ToolCallCard with a compact BasicTool-style primitive and grouped context rows

The current `ToolCallCard` is too heavy and detail-first. It should be replaced or split into a compact primitive that emphasizes semantic labeling over raw payloads. Each tool row should show:

- icon and normalized tool label
- short title and optional subtitle or target
- status treatment tuned for `pending`, `running`, `completed`, `error`, `cancelled`
- optional argument chips or inline descriptors
- expandable detail only after completion or error

Context tools (`read`, `glob`, `grep`, `search`, memory browse/read/search, and similar lookup steps) should not appear as a flat repeated list when they are contiguous. The display adapter should aggregate them into a `ContextToolGroupPart` with a summary such as `Gathering context · 3 reads · 2 searches`, with nested compact rows only when expanded.

`todowrite` should be filtered from the main transcript by default. Unknown tools should only appear if they have user-meaningful input/output that cannot be classified into a better tool kind.

**Alternatives considered:**

- Keep `ToolCallCard` and restyle it. Rejected because its API and default behavior are built around raw input/output panes and event-card affordances.
- Hide all tool calls except diffs. Rejected because tools are useful evidence in the transcript; the problem is presentation, not their existence.

### D6: Treat patch, edit, and write output as file-change evidence with turn-ending diffs

Patch-related tools should not remain primarily raw patch viewers. The system will normalize file operations into file-centric change records used in two places:

- inline tool rows such as `Patch design.md +84 -0`
- a turn-ending `TurnDiffs` section listing all changed files from that turn

The backend should provide file metadata whenever available: relative path, operation type, additions, deletions, old path for renames, and raw patch or before/after payloads for fallback detail. The frontend can still derive from patch text when necessary, but server-provided metadata should win.

For multi-file patches, the tool trigger should summarize the count and allow expansion into per-file accordions. For `edit` and `write`, the tool row should prefer filename and path context, with expandable diff or content blocks. Large diff bodies should lazy mount and remain collapsed by default.

This makes the transcript answer the user question "what changed?" without forcing them to inspect raw patch payloads.

**Alternatives considered:**

- Keep diffs only inside tool details. Rejected because changed files are the most important artifact of the turn and belong in the turn summary itself.
- Add a separate page-level changed-files dashboard. Rejected because the requirement is `Diff belongs to the turn`, not a detached artifact pane.

### D7: Separate transcript display status from runtime status and use subtle live affordances

The page currently mixes operational status, liveness, and transcript state in one header model. The redesign should keep runtime status for correctness but derive a transcript display state for UI behavior. At minimum, the page should distinguish:

- loading
- replaying
- live-streaming
- finalizing
- completed
- stale
- failed

The sticky title uses this state to show a small spinner, subtle progress bar, and restrained wording. The transcript itself uses this state to decide whether to show thinking shimmer, whether the turn is auto-followed, and whether the jump-to-bottom affordance appears.

This avoids treating every running session as identical and supports the desired low-noise live feel.

**Alternatives considered:**

- Keep a single `running/completed/failed` badge model. Rejected because it cannot express replay vs active streaming vs finalizing, and it encourages the UI to signal activity by spamming tool rows.
- Derive all live behavior from SSE presence alone. Rejected because sessions can be stale, probing, or temporarily quiet while still logically active.

### D8: Reimplement auto-follow around transcript semantics rather than scroll position alone

The jump-to-bottom and follow behavior should be rebuilt with the same interaction model as opencode-style transcript readers:

- auto-follow only while the user remains bottom-locked
- stop auto-follow when the user scrolls away or selects text
- ignore nested scroll containers marked with `data-scrollable`
- preserve bottom lock on resize when the user was already following
- show jump-to-bottom only when the user is meaningfully away from the bottom and new content exists

`useSessionTranscript.ts` should own a small scroll-state machine instead of a single `isNearBottom` boolean. The transcript container and expandable tool/diff panes should participate through attributes rather than ad hoc event checks.

**Alternatives considered:**

- Keep the current near-bottom flag and absolute button. Rejected because it is not enough to avoid disruptive jumps during reading, selection, or nested diff scrolling.
- Disable auto-follow entirely for live sessions. Rejected because it would make active sessions feel inert and force manual scrolling during normal tailing.

## Risks / Trade-offs

- [Replay/live normalization diverges during rollout] → Move shared normalization helpers close to the transcript service contract and add adapter-level tests that compare live-updated turns with replayed turns.
- [Hiding internal tools may remove information some users still need] → Keep raw input/output accessible in expanded details or a debug-only path, while keeping the default transcript low-noise.
- [Patch metadata from historical sessions is incomplete] → Prefer server metadata when present, fall back to existing patch parsing, and render a generic patch summary when exact file stats cannot be recovered.
- [Large transcripts and diffs regress performance] → Use lazy mounting, `content-visibility`, collapsed detail panes, and compact summaries by default.
- [opencode visual fidelity drifts because Mohist uses different primitives] → Mirror opencode interaction and structure first, then tune styling tokens in one dedicated transcript component set instead of scattering class changes.
- [Status terminology becomes inconsistent across issue page and session page] → Treat the transcript page as having a display-status layer that maps from runtime status, and document that mapping in shared types.

## Migration Plan

1. Extend transcript domain types to support normalized tool metadata and display adapter output without removing the existing API shape.
2. Update `session-transcript-service.ts` to merge tool lifecycle events more aggressively, normalize statuses, suppress empty unknown placeholders, and attach file-change metadata.
3. Refactor `useSessionTranscript.ts` to consume the stronger normalization contract and funnel live updates through the same display adapter used for replayed turns.
4. Introduce new transcript components for sticky title, turn list, assistant part rendering, compact tool rows, context groups, and turn-ending diffs.
5. Replace `SessionTranscriptView` usage in `SessionPage.tsx` with the new transcript layout and scroll-follow controller.
6. Keep old tool-detail fallbacks available during rollout so incomplete metadata does not break transcript readability.
7. Verify with historical sessions, live running sessions, interrupted sessions, and multi-file patch sessions to ensure refresh and live views converge.
8. Rollback strategy: revert `SessionPage` to the previous transcript renderer while leaving backend normalization improvements in place, since they are backwards-compatible improvements to the transcript payload.

## Open Questions

- Should hidden internal tools be controlled only by a frontend allow/deny policy in the adapter, or should the backend emit an explicit `visibility` hint per normalized tool?
- Should `reasoning` markdown remain collapsed by default for completed turns, or expand automatically when it is the only visible assistant content?
- For `write` operations without a patch, should the expanded detail show full content, a synthetic diff against empty content, or just a file summary with optional raw view?
