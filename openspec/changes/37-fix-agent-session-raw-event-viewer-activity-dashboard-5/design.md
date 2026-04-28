## Context

The Agent Session panel (`SessionTimeline.tsx` + `useSessionTimeline.ts`) renders agent activity from two data sources:

1. **Historical**: `GET /api/issues/:number/logs` → `reconstructRoundsFromLogs()` rebuilds rounds from `workflow_log` table
2. **Live**: SSE events dispatched through `useSSE.ts` → `agent-events.ts` EventTarget → `useSessionTimeline` subscriptions

The backend already has a working event pipeline:
- Build stage: `RalphExecutor` emits `ralph_task_update` and `ralph_loop_progress` via `eventBus`
- Build stage: `acp-session.ts` emits `coder_tool_call` and `coder_text_chunk` directly
- Plan/Review stage: `workflow-controller.ts` bridges all ACP `sessionUpdate` notifications (including `tool_call`, `tool_call_update`, `agent_thought_chunk`) as `plan_session_update` via `onSessionUpdate` callback

The SSE layer (`events.ts` ALL_EVENT_TYPES, `useSSE.ts` eventTypes, `agent-events.ts` AGENT_DETAIL_EVENTS) already includes `ralph_task_update` and `ralph_loop_progress` in registration arrays. `useSSE.ts` already calls `dispatchAgentEvent()` for all agent detail events.

**The gaps are all in frontend consumption**, not backend emission.

## Goals / Non-Goals

**Goals:**
- Fix the 5 pipeline gaps so tool calls show meaningful context, task progress is visible, thoughts are collapsed, and historical data quality matches live
- All changes are frontend-only (no backend changes required)
- Minimal disruption to existing component hierarchy

**Non-Goals:**
- Backend-side title derivation for `tool_call` initial events (P2, deferred — frontend derivation is sufficient)
- Removing the dead `main_tool_call` event type (harmless, can clean up later)
- Redesigning the timeline component layout or adding filtering/search

## Decisions

### D1: Derive tool call titles purely on the frontend

Add a shared `deriveToolCallTitle(toolName, title, rawInput)` function used in three places: `ToolCallTimelineEntry` rendering, `reconstructRoundsFromLogs`, and the live `coder_tool_call` handler in `useSessionTimeline`. This avoids backend changes and immediately fixes both live and historical display.

**Logic:** Parse `rawInput` as JSON, extract field by tool name:
- `read`/`read_file`/`write`/`write_file`/`edit` → `file_path` or first string arg → basename
- `bash` → `command` field or first string arg
- `glob`/`search_files` → `pattern` field
- `grep`/`search` → `pattern` field
- Fallback: if `title` differs from `toolName`, use it; otherwise `toolName`

**Alternatives considered:**
- Backend derivation at `workflowLogRepo.insert()` time: would fix historical data permanently but requires backend changes + migration of existing rows. Overkill for this fix.
- Derivation only in `ToolCallTimelineEntry` rendering: would miss updating the `title` field on the entry itself, causing inconsistency between what's displayed and what's stored.

### D2: Handle Plan stage tool calls via plan_session_update (no new event type)

The `onSessionUpdate` bridge in `workflow-controller.ts` already forwards ALL ACP sessionUpdate types (including `tool_call` and `tool_call_update`) as `plan_session_update` events. The frontend's `flushPlanBuffer` currently only processes `sessionUpdate === 'agent_message_chunk'`. Extend it to also handle `tool_call`, `tool_call_update`, and `agent_thought_chunk`.

This means Plan/Review stage tool calls travel through the existing `plan_session_update` channel — no new SSE event type needed.

**Alternatives considered:**
- Emit `main_tool_call` separately from the `onSessionUpdate` bridge: would add a parallel channel but duplicate the tool call data that already flows through `plan_session_update`.
- Remove `main_tool_call` type entirely: harmless cleanup but out of scope.

### D3: Add `thoughtText` field to Round interface

Extend the `Round` interface with `thoughtText: string`. Both `reconstructRoundsFromLogs` (historical) and `flushPlanBuffer` (live) accumulate thought text into this field separately from `agentText`. The `RoundSection` component renders `thoughtText` in a default-collapsed `<details>` element.

**Alternatives considered:**
- Separate `ThoughtText` component with React state: adds unnecessary component complexity for a simple show/hide toggle. `<details>/<summary>` HTML elements handle this natively.
- Filter thoughts entirely (don't store them): loses debugging information. Users may want to expand thoughts when diagnosing agent behavior.

### D4: Task progress state managed in useSessionTimeline via useState

Add `useState<Map<string, TaskProgressEntry>>` and `useState<LoopProgress | null>` to `useSessionTimeline`. Subscribe to `ralph_task_update` and `ralph_loop_progress` via `onAgentEvent`. Expose via return value. The `SessionTimeline` component passes these as props to a new `TaskProgressPanel` rendered conditionally when `currentStage === 'build'`.

**Alternatives considered:**
- Separate `useTaskProgress` hook: adds an extra hook call in the page component, but the task progress data is tightly coupled with session timeline context (same issue, same lifecycle). Keeping it in `useSessionTimeline` is simpler.
- Zustand store for task progress: overkill for data scoped to a single page view.

### D5: Completed coder_tool_call events propagate title and rawInput

In the `coder_tool_call` handler (line 301-318), when `detail.state !== 'started'`, spread the existing entry but also copy `detail.title` and `detail.rawInput` from the event payload. Same fix in `reconstructRoundsFromLogs` for `tool_call_update` entries (line 92-97).

This is a 2-line fix in each location — the event payload already carries these fields, they're just being ignored.

### D6: TaskProgressPanel as inline component in SessionTimeline.tsx

Add `TaskProgressPanel` as a function component within `SessionTimeline.tsx` (same file). It receives `tasks: TaskProgressEntry[]` and `loopProgress: LoopProgress | null`. Renders a compact grid of task IDs with status icons and a summary line.

No separate file needed — the component is ~50 lines and only used inside `SessionTimeline`.

## Risks / Trade-offs

- **[Plan session_update flush buffer grows with tool calls]** → The RAF-flushed `planBufferRef` now also accumulates tool call events. Tool calls are low-frequency (~1/sec), so the 100ms flush interval handles this fine. No change to flush logic needed.
- **[thoughtText accumulation on rounds increases memory]** → Thought text can be 15KB+. Default-collapsed `<details>` element means the DOM is rendered but hidden. For extremely long sessions, this could cause sluggishness. Mitigation: cap `thoughtText` at 20KB with truncation indicator (same pattern as `MAX_AGENT_TEXT_LENGTH` in backend).
- **[deriveToolCallTitle fails on unexpected rawInput formats]** → The function must handle malformed JSON, missing fields, and non-object rawInput gracefully. Always fall back to `toolName`. Test with real ACP event payloads.
- **[Task progress state survives page navigation without reset]** → The `useSessionTimeline` hook unmounts on page change, which resets task progress. On re-mount, historical data doesn't include task progress (no workflow_log entries for `ralph_task_update`). Task progress is only available during live streaming. This is acceptable — task completion status is visible from round labels.

## Migration Plan

No backend changes, no database migration, no API changes. Deploy is frontend-only:

1. Add `deriveToolCallTitle()` utility to `useSessionTimeline.ts` (or a shared util)
2. Add `TaskProgressEntry`, `LoopProgress` types to `types.ts`
3. Extend `Round` interface with `thoughtText`
4. Update `reconstructRoundsFromLogs`: handle `agent_thought_chunk`, propagate title/rawInput on completed, derive titles
5. Update `flushPlanBuffer`: handle `tool_call`, `tool_call_update`, `agent_thought_chunk`
6. Update `coder_tool_call` handler: propagate title/rawInput on completed
7. Add `ralph_task_update` / `ralph_loop_progress` subscriptions in `useSessionTimeline`
8. Add `TaskProgressPanel` component in `SessionTimeline.tsx`
9. Update `RoundSection` to render `thoughtText` in collapsed `<details>`
10. Update `ToolCallTimelineEntry` to use `deriveToolCallTitle` for display

No rollback needed — all changes are additive frontend rendering.

## Open Questions

None. All 5 gaps have clear, frontend-only solutions.
