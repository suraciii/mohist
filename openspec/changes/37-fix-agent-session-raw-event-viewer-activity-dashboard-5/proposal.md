## Why

The Agent Session panel is a raw event dump — tool calls display as "read read" with no file path context, 15KB+ thinking text floods the view unfiltered, Build stage task progress (T-001~T-008) is invisible, and refreshing the page yields worse data quality than real-time. Five data pipeline gaps (dead `main_tool_call` event, completed title discarded, `ralph_task_update` unconsumed, text unclassified, history/real-time quality asymmetry) prevent the panel from being a usable activity dashboard.

## What Changes

- Fix `useSessionTimeline.ts` completed event handling to propagate `title` + `rawInput` updates from `tool_call_update` (断层 2)
- Derive meaningful display titles from `rawInput`: `read` → file path, `bash` → command string, `glob` → pattern (断层 2)
- Subscribe frontend to `ralph_task_update` and `ralph_loop_progress` SSE events; add task progress state to `useSessionTimeline` (断层 3)
- Add `TaskProgressPanel` component showing Build stage task list with status indicators (passed/pending/running/failed) (断层 3)
- Separate `agent_thought_chunk` from `agent_message_chunk` in timeline rendering; default-collapse thinking text (断层 4)
- Fix `reconstructRoundsFromLogs` to derive meaningful titles from `rawInput` when initial `title` is just the tool name (断层 5)
- Emit `main_tool_call` events from Plan/Review stage ACP sessions via the existing `onSessionUpdate` bridge, or remove the dead event type entirely (断层 1)

## Capabilities

### New Capabilities

- `build-task-progress-ui` — TaskProgressPanel component + frontend subscription to `ralph_task_update` / `ralph_loop_progress` events, rendering Build stage task progress with status indicators
- `tool-call-context-display` — Derive and display meaningful context from tool call rawInput (file paths for read, commands for bash, patterns for glob) in both live SSE and historical reconstruction

### Modified Capabilities

- `session-timeline-ui` — Round rendering must separate thought vs message text, default-collapse thoughts, and propagate completed event title/rawInput updates
- `pipeline-session-events` — Plan/Review stage ACP tool calls need a real-time channel (either emit `main_tool_call` or reuse existing event bridge); frontend must subscribe to `ralph_task_update` and `ralph_loop_progress`
- `workflow-log` — Frontend `reconstructRoundsFromLogs` derives meaningful titles from rawInput to achieve parity with live SSE data (backend title derivation deferred to P2)

## Impact

- **Frontend**: `useSessionTimeline.ts` (event handling, state shape), `SessionTimeline.tsx` (new sub-components, thought collapsing), `types.ts` (new event types, task progress types)
- **Backend**: No backend changes required — all gaps are in frontend consumption
- **API**: No new endpoints; changes are SSE event consumption and workflow_log data quality
- **Existing specs**: `session-timeline-ui`, `pipeline-session-events`, `workflow-log` spec requirements are modified
