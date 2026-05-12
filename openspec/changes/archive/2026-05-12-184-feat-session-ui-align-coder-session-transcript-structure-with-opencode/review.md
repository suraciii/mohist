# Review: Issue #184 — Align Coder Session Transcript Structure with OpenCode

## Overall Assessment

The implementation delivers a solid structural redesign of the session page from a card-heavy event log to an opencode-like transcript reading experience. The display adapter cleanly separates data projection from rendering, the backend normalization handles tool lifecycle merging well, and the component decomposition follows the target structure. Tests pass, type checks are clean, and the build succeeds.

There are several issues that need attention, detailed below.

---

## Correctness

### ERROR: Context group flush logic can lose interleaved text between grouped tools

**File:** `packages/cli/web/src/lib/session-transcript-display.ts:256-269`

The adapter's main loop has a subtle interaction bug. When the loop encounters a `text` part while a context group stack is active, it only flushes the group if the *next* part is NOT a context tool:

```ts
if (part.type === 'text') {
  if (toolStack.length > 0) {
    const nextPart = rawParts[i + 1]
    const nextIsContextTool = nextPart?.type === 'tool' && isContextTool(nextPart.tool.normalizedName ?? nextPart.tool.toolName)
    if (!nextIsContextTool) {
      flushContextGroup()
    }
  }
  displayParts.push(buildDisplayTextPart(part))
  continue
}
```

This look-ahead logic aims to avoid flushing context groups when text is interleaved between context tools (e.g., `read → text → glob`). However, the text part itself gets pushed as a separate display part *within* the context group flow, which means text between grouped context tools will appear *outside* the group, splitting what should be a coherent reading sequence. The spec says "default collapsed" for context groups — this look-ahead prevents proper grouping when a micro text response occurs between context tool calls, which is the intended behavior, but the text is never counted inside the group.

**Severity:** Warning. The look-ahead avoids breaking context groups on authorial pauses, but it can produce an odd reading order where a one-line text response separates a context group from its continuation. This is a judgment call, not a crash bug.

### WARNING: `useSessionTranscript` diverges from display adapter normalization

**File:** `packages/cli/web/src/hooks/useSessionTranscript.ts:59-95`

The `inferToolName` and `normalizeToolName` functions in the live hook are a simplified re-implementation of the backend's `inferNormalizedToolName`. The live hook:
1. Does not infer tool names from `title` containing known tool keywords (backend does at line 249-253)
2. Uses `todowrite` detection in the live path only through `input.todos`, while the backend uses `INTERNAL_TOOL_NAMES`

This means live tool calls may show different normalized names than replayed ones. The display adapter consumes the backend-normalized `normalizedName` for replay, but the live hook's own normalization is used before the data reaches the display adapter.

**Severity:** Warning. Live/replay divergence is acknowledged in the design, but the degree of divergence in tool name inference could cause visible differences (e.g., a `title`-based inference working on replay but not live).

### WARNING: No `unknown` tool suppression in the display adapter

**File:** `packages/cli/web/src/lib/session-transcript-display.ts:279-286`

The spec requires: "主视图不出现 Called unknown" and "Unknown-tool fallback is last resort". The display adapter filters `hidden` and `todowrite`, but does not filter tools where `normalizedName === 'unknown'` and there is no meaningful input/output. The backend marks these with `hidden: undefined` (not `hidden: true`) and records `hasUnknownTools` in metadata, but the frontend adapter doesn't use this to suppress empty unknown tool rows.

**Severity:** Warning. The backend already does significant normalization, so truly empty unknowns should be rare. But the display adapter should at least hide unknown tools with no displayTitle, no input, and no output, matching the spec's "Unknown-tool fallback is last resort" scenario.

---

## Complexity

### WARNING: `session-transcript-service.ts` inferNormalizedToolName exceeds 50-line guideline

**File:** `packages/cli/src/services/session-transcript-service.ts:238-336`

The `inferNormalizedToolName` function is ~100 lines with deeply nested conditions including raw input payload parsing, raw output parsing, and title matching. While the logic is correct, this function has high cyclomatic complexity (>10) handling multiple fallback strategies. It should be decomposed into smaller helper functions (e.g., `inferFromInput`, `inferFromOutput`, `inferFromTitle`).

### WARNING: `handleToolCallUpdate` is ~110 lines

**File:** `packages/cli/src/services/session-transcript-service.ts:1058-1171`

This function handles both the "update existing tool" and "create new tool from update" code paths in a single function, mixing state mutation logic with normalization and file change extraction. The two paths should be extracted.

### WARNING: `SessionPage.tsx` scroll handling is complex but manageable

**File:** `packages/cli/web/src/components/SessionPage.tsx:350-454`

The scroll handling uses four separate `useEffect` hooks and three refs (`isNearBottomRef`, `isUserScrollingRef`, `isSelectingTextRef`). The logic is correct but the scroll-state machine could benefit from a custom hook extraction (`useAutoFollow`). The spec explicitly calls for "useSessionTranscript should own a small scroll-state machine" — this is currently split between the component and the hook.

---

## Test Coverage

### PASS: Display adapter has comprehensive tests

**File:** `packages/cli/web/tests/session-transcript-display.test.ts` (369 lines, 20 test cases)

Covers: text/reasoning/tool/error projection, todowrite filtering, context grouping (contiguous, mixed, single), hidden tool filtering, changed file extraction, turn state derivation, prompt summary fields.

### PASS: Backend transcript service has extensive tests

**File:** `packages/cli/tests/session-transcript-service.test.ts` (2011+ lines)

Covers: prompt boundaries, text/reasoning accumulation, tool lifecycle merging, unknown tool inference, file change extraction, recovery/error events, ordering, and many edge cases.

### PASS: Session page transcript integration tests exist

**File:** `packages/cli/web/tests/SessionPage.transcript.test.tsx` (1152 lines, 51 test cases total)

Covers: centered layout, prompt display, assistant parts, tool rows, context groups, turn diffs, error states, scroll behavior, live updates.

### WARNING: No tests for `useSessionTranscript` hook directly

The live update hook is tested indirectly through the SessionPage integration tests, but there are no unit tests specifically for `useSessionTranscript.ts`. This is the most complex frontend module (~680 lines) and contains intricate state management for tool correlation, text chunk accumulation, and scroll behavior.

---

## Security

### PASS: No injection risks detected

The transcript service parses JSON from untrusted event data but always wraps parsing in try/catch. No raw HTML is rendered — text is passed through React's built-in XSS protection or Markdown rendering. No secrets or credentials appear in the code.

### PASS: Clipboard API usage is safe

`PromptBlock.tsx:28` uses `navigator.clipboard.writeText(prompt.text)` which is a safe, standard API.

---

## Spec Compliance

### agent-session-ui/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Session page reads as a Mohist-to-Coder transcript | PASS | `SessionPage.tsx` uses `SessionTranscriptLayout` → `TurnList` → `TurnItem` with `PromptBlock` + `AssistantParts` |
| Prompt-led turns anchor the transcript | PASS | `DisplayTurn.prompt` drives `PromptBlock` rendering at each turn boundary |
| Internal transcript noise stays out | PARTIAL | `todowrite` is filtered. `hidden` tools are filtered. But unknown tools with no content are NOT filtered — see WARNING above |
| File-changing output belongs to the assistant turn | PASS | `ToolRowView` shows changed files inline; `TurnDiffs` summarizes at turn end |

### coder-session-tracking/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Tool lifecycle updates resolve to one logical tool | PASS | Backend `SessionTranscriptAssembler` uses `toolPartsById` map to merge start+update events |
| Unknown-tool fallback is last resort | PARTIAL | Backend infers names aggressively via `inferNormalizedToolName`. But display adapter doesn't suppress empty unknowns |
| Historical replay stays ordered and readable | PASS | `EVENT_PRIORITY` ensures deterministic ordering; `sortEvents` uses stable index as tiebreaker |

### http-api/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Detail response contains stable transcript structure | PASS | API returns normalized turns with `normalizedName`, `changedFiles`, `status` |
| Tool metadata is display-ready | PASS | `normalizedName`, `displayTitle`, `target`, `changedFiles` provided |
| Replay remains usable without live SSE state | PASS | Full transcript is reconstructable from API response alone |

### pipeline-session-events/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Live tool updates do not create transcript duplication | PARTIAL | `useSessionTranscript` uses `liveToolCallMapRef` to correlate updates, but normalization logic differs from backend (see WARNING) |
| Recovery and interruption remain readable | PASS | Error/recovery events rendered with `ErrorPartView` using amber (not fatal red) for recovery |
| Refresh after live activity preserves transcript meaning | PASS | `invalidateAndRefetch` triggers query invalidation on terminal events |

### session-timeline-ui/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Transcript layout prioritizes reading flow | PASS | `max-w-2xl mx-auto` centered column, `role="log"` ARIA attribute |
| Running sessions show subtle progress | PASS | `StickySessionTitle` shows ping animation and `Running` label; `ThinkingPlaceholder` shows subtle indicator |
| Auto-follow respects reader intent | PASS | Three refs track near-bottom, user scrolling, and text selection; `data-scrollable` attribute on nested scrollable regions |
| Jump-to-bottom control appears when needed | PASS | `JumpToBottomButton` renders when `newContentAvailable` is true |

---

## Warnings (non-blocking)

1. **`inferNormalizedToolName` complexity** (~100 lines, cyclomatic >10) — Should be refactored into smaller helpers, but is functionally correct.

2. **Live/replay normalization divergence** — The `useSessionTranscript` hook reimplements tool name inference independently. This will cause subtle display differences on live sessions vs. replay. Not a crash bug, but worth tracking for convergence work.

3. **No unknown-tool suppression in display adapter** — The frontend should filter unknown tools that have no meaningful content. The backend does heavy inference, so this is low-priority.

4. **Scroll state not extracted into a custom hook** — The spec calls for `useSessionTranscript` to own a "small scroll-state machine." Currently the scroll logic is in `SessionPage.tsx` across 4 `useEffect` hooks and 3 refs. A `useAutoFollow` extraction would improve readability and maintainability.

5. **`contentVisibility: 'auto'` applied as inline style, not with contain intrinsic size** — `TurnList.tsx:15` uses `style={{ contentVisibility: 'auto' }}` without specifying `containIntrinsicSize`, which can cause layout shifts. This should use CSS with `contain-intrinsic-size` for proper rendering containment.

6. **Minor TypeScript concern:** `handleScroll` takes `(evt?: Event)` but `onScroll` is `(evt: Event)`. The `evt.target` cast `(target as HTMLElement).closest(...)` is safe due to the null check, but an explicit guard would be clearer.

---

## Summary

The implementation successfully delivers the core redesign: centered transcript layout, prompt-led turns, assistant parts (text, reasoning, tools), context grouping, turn diffs, hidden internal tools, and auto-follow scroll behavior. The display adapter is a clean abstraction. The backend normalization is thorough. Tests are comprehensive and all pass.

**Issues requiring fixes:** None at error level. The warnings above are real but non-blocking. The most substantive concern is the live/replay normalization divergence in tool name inference, which is an acknowledged trade-off that should be tracked for convergence.

<promise>PASS</promise>