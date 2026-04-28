# Review Report

## Verdict: FAIL

## Dimensions

### Correctness: FAIL

- **Bug: `deriveToolCallTitle` non-JSON fallback returns `toolName` instead of `rawInput`** — `useSessionTimeline.ts:47`. When `rawInput` is a plain string like `'npm test'` (not valid JSON), `JSON.parse` throws, the catch block returns `toolName` (e.g., `'bash'`), but the spec and acceptance criteria require returning the raw string directly. Verified: `deriveToolCallTitle('bash', 'bash', 'npm test')` returns `'bash'` instead of `'npm test'`.
- Build passes with no TypeScript errors.
- Pre-existing test failures (6 files, 32 tests in `recover-issues`, `merge-queue`, `pipeline-checkpoint`, `agent-runner-service`) are unrelated to this change.

### Complexity: PASS

- `useSessionTimeline.ts` is 472 lines — large but each function is focused and under 50 lines.
- `SessionTimeline.tsx` is 419 lines — `TaskProgressPanel`, `TaskStatusIcon`, and `RoundSection` are well-decomposed.
- No duplicated logic. `deriveToolCallTitle` is reused in both `reconstructRoundsFromLogs` and `flushPlanBuffer`.

### Test Coverage: FAIL

- No unit tests for `deriveToolCallTitle` despite it being a pure, exported function with clearly specified acceptance criteria.
- No tests for `reconstructRoundsFromLogs` title derivation behavior.
- No tests for new SSE event subscriptions (`ralph_task_update`, `ralph_loop_progress`).
- T-007 (verification task) confirmed build passes but did not add any test coverage.

### Security: PASS

- `JSON.parse` is properly wrapped in try-catch.
- React handles rendering (no XSS risk).
- No secrets or credentials exposed.
- SSE event data is filtered by `issueId`.

### Spec Compliance: FAIL

#### T-001: Add deriveToolCallTitle utility and new types

| Criterion | Result | Notes |
|-----------|--------|-------|
| `deriveToolCallTitle('read', 'read', '{"file_path":"packages/cli/src/server.ts"}')` returns `'server.ts'` | PASS | |
| `deriveToolCallTitle('bash', 'bash', '{"command":"npm run build"}')` returns `'npm run build'` | PASS | |
| `deriveToolCallTitle('read', 'packages/cli/src/main.ts', '...')` returns `'packages/cli/src/main.ts'` | PASS | |
| `deriveToolCallTitle('bash', 'bash', 'npm test')` returns `'npm test'` | **FAIL** | Returns `'bash'` — catch block returns `toolName` instead of `rawInput` |
| `deriveToolCallTitle('unknown', 'unknown', null)` returns `'unknown'` | PASS | |
| `TaskProgressEntry` and `LoopProgress` types exist in `types.ts` | PASS | |
| `Round` interface has `thoughtText: string` field | PASS | |
| Typecheck passes | PASS | |

#### T-002: Fix completed event title/rawInput propagation

| Criterion | Result | Notes |
|-----------|--------|-------|
| `reconstructRoundsFromLogs`: tool_call_update completed copies title and rawInput | PASS | Lines 134-135 |
| `reconstructRoundsFromLogs`: tool_call with title==='read' derives title from rawInput | PASS | Line 147 |
| `reconstructRoundsFromLogs`: tool_call_update title overrides derived title | PASS | Line 134 |
| Live coder_tool_call completed propagates detail.title and detail.rawInput | PASS | Lines 395-396 |
| Historical page load shows 'server.ts' instead of 'read' | PASS | (Verified via code) |

#### T-003: Handle plan_session_update tool_call, tool_call_update, agent_thought_chunk

| Criterion | Result | Notes |
|-----------|--------|-------|
| `flushPlanBuffer` handles `sessionUpdate==='tool_call'` | PASS | Lines 225-244 |
| `flushPlanBuffer` handles `sessionUpdate==='tool_call_update'` | PASS | Lines 245-263 |
| `flushPlanBuffer` handles `sessionUpdate==='agent_thought_chunk'` | PASS | Lines 219-224 |
| `reconstructRoundsFromLogs` handles `agent_thought_chunk` → thoughtText | PASS | Lines 112-118 |

#### T-004: ralph_task_update and ralph_loop_progress subscriptions

| Criterion | Result | Notes |
|-----------|--------|-------|
| `onAgentEvent('ralph_task_update')` subscription added | PASS | Lines 414-438 |
| `onAgentEvent('ralph_loop_progress')` subscription added | PASS | Lines 440-449 |
| status 'started' → 'running' | PASS | |
| status 'completed' → 'passed' | PASS | |
| status 'failed' stores error and attempt | PASS | |
| loopProgress state updates | PASS | |
| taskProgress and loopProgress exposed in return | PASS | |

#### T-005: TaskProgressPanel component

| Criterion | Result | Notes |
|-----------|--------|-------|
| Renders when currentStage is 'build' and taskProgress non-empty | PASS | Line 404 |
| Hidden when currentStage is not 'build' | PASS | |
| Summary line shows 'X/Y passed' format | PASS | Line 333 |
| Each task shows task ID with correct status icon | PASS | Green/blue/red/orange verified |
| Failed tasks display error message | PASS | Lines 343-353 |
| SessionTimelineProps includes taskProgress and loopProgress | PASS | |
| Parent passes new props from useSessionTimeline | PASS | IssueDetailPage.tsx lines 551-552 |

#### T-006: Thought text collapsible section

| Criterion | Result | Notes |
|-----------|--------|-------|
| thoughtText in `<details>` collapsed by default | PASS | |
| **Summary label shows "Thinking..."** | **FAIL** | Shows `"Thinking (0.0KB)"` — missing ellipsis `"..."` and always shows size |
| **Size indicator only when > 500 chars** | **FAIL** | Size indicator always shown, spec requires it only when > 500 chars |
| Clicking toggle expands thought content | PASS | |
| Message text renders normally above | PASS | |
| ToolCallTimelineEntry uses deriveToolCallTitle for display label | **FAIL** | `ToolCallTimelineEntry` at line 79 renders `entry.title` directly — does NOT call `deriveToolCallTitle`. The hook sets title via deriveToolCallTitle at insertion time, which is functionally equivalent for most cases, but if title is set to toolName by deriveToolCallTitle (e.g., unknown tool), the component doesn't re-derive it |

## Fix Suggestions

1. **`packages/cli/web/src/hooks/useSessionTimeline.ts:47`** — Change catch block from `return toolName` to `return rawInput || toolName` to handle non-JSON rawInput strings (e.g., `'npm test'`).

2. **`packages/cli/web/src/components/SessionTimeline.tsx:257-258`** — Change `"Thinking"` to `"Thinking..."` and conditionally show size indicator only when `thoughtText.length > 500`:
   ```tsx
   <summary className="text-xs text-gray-400 cursor-pointer hover:text-gray-600 select-none">
     Thinking...{round.thoughtText.length > 500 ? ` (${(round.thoughtText.length / 1024).toFixed(1)}KB)` : ''}
   </summary>
   ```

3. **`packages/cli/web/src/components/SessionTimeline.tsx:79`** — Add `deriveToolCallTitle` import and use it to compute display title instead of raw `entry.title`, ensuring consistent derivation even for entries where title was set to toolName:
   ```tsx
   {entry.title && (
     <span className="text-xs text-gray-500 truncate">
       {deriveToolCallTitle(entry.toolName, entry.title, entry.rawInput)}
     </span>
   )}
   ```
   Or alternatively, remove the title check and always render the derived title.
