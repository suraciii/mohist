## Context

The SessionList + SessionDetail components live in the IssueDetailPage's right column (`lg:col-span-1`). Each session expands inline with `max-h-[400px]`, showing a flat tool call list via `ToolCallTimelineEntry`. There's no route, no diff view, and no conversational flow.

The data layer is already in good shape:
- `useCoderSessions(issueNumber)` fetches `CoderSessionItem[]` via `GET /api/issues/:number/coder-sessions`
- `useSessionTimeline(issueNumber, session?)` has dual mode: without `session` it fetches all workflow logs; with `session` it uses `session.workflowLogs` for history + SSE for live events
- `reconstructRoundsFromLogs()` parses `WorkflowLogItem[]` into `Round[]` with `agentText`, `thoughtText`, and `ToolCallEntry[]`
- `ToolCallEntry` already carries `rawInput`, `rawOutput`, `toolName`, `title`, `state`, `duration`

The router (`App.tsx`) uses react-router-dom v6 with `<Routes>` nested under `<ProjectGuard>`. Current routes: `/`, `/issue/:number`, `/explore`, `/explore/:id`, `/settings`, `/logs`.

## Goals / Non-Goals

**Goals:**
- Dedicated full-page session view at `/issue/:number/session/:sessionId`
- Conversational round rendering with tool-specific display (edit diffs, bash terminals, collapsed summaries)
- SessionHeader as navigation link from issue page
- Live SSE streaming support on the session page
- Breadcrumb navigation back to the parent issue

**Non-Goals:**
- Backend API changes (all data already available)
- Modifying the Explore session pages (separate concept)
- File-level diff view with syntax highlighting (use basic line coloring)
- Mobile-first optimization (desktop-first, basic mobile support)
- Search/filter within a session

## Decisions

### D1: SessionPage reuses `useSessionTimeline(issueNumber, session)` in session mode

The hook already supports a `session?: CoderSessionItem` parameter. When passed, it uses `session.workflowLogs` for history instead of fetching all workflow logs. The session page will fetch the single session from `useCoderSessions` and pass it through.

**Why:** Avoids duplicating the round reconstruction logic. The hook already handles SSE subscription with `coderSessionId`/`acpSessionId` filtering when in session mode.

**Implementation:**
1. `SessionPage` extracts `number` and `sessionId` from URL params
2. Uses `useCoderSessions(issueNumber)` to get the session list
3. Finds the matching session by `session.id === sessionId`
4. Passes it to `useSessionTimeline(issueNumber, session)` which handles history + live SSE

### D2: Tool-specific rendering via a `ToolCallCard` component with variant prop

Instead of modifying the existing `ToolCallTimelineEntry`, create a new `ToolCallCard` component that renders differently based on `toolName`:
- `edit` / `write` → inline diff (extract `oldString`/`newString`/`filePath` from `rawInput`)
- `bash` → terminal block (extract `command` from `rawInput`, output from `rawOutput`)
- `read`, `glob`, `grep`, `todowrite`, `webfetch`, `memread`, `membrowse`, `memsearch` → collapsed summary line
- Unknown → generic summary with expandable raw input/output

The existing `ToolCallTimelineEntry` remains unchanged for the inline issue-page context. The new `ToolCallCard` is used only in `SessionPage`.

**Why:** The inline view on the issue page should keep its current compact behavior. The session page has different UX needs (full diff, terminal output). Keeping them separate avoids conditional complexity in a single component.

**Alternatives considered:**
- Adding a `mode="inline" | "page"` prop to the existing component — would bloat a single component with divergent rendering logic
- A render prop / slot pattern — overkill for two contexts

### D3: Diff extraction from `rawInput` JSON

The `edit` tool's `rawInput` is a JSON string containing `{file_path, oldString, newString}`. The `write` tool's `rawInput` contains `{filePath, content}`. Both are already available in `ToolCallEntry.rawInput`.

For the diff view, parse `rawInput` to extract old/new strings and render them with line-by-line coloring (red `-` / green `+`). No unified diff algorithm needed — just split both strings by `\n` and color-code them.

**Why:** The raw data is already there. A simple line-by-line display is sufficient for understanding what changed. Full unified diff (with context lines, hunk headers) is a non-goal.

### D4: SessionHeader becomes `<Link>`, SessionDetail becomes summary-only

Convert `SessionHeader` from `<button onClick={toggle}>` to `<Link to={/issue/:number/session/:sessionId}>`. Replace the expanded `SessionDetail` with a one-line summary showing "N files changed · M tool calls".

The summary derives from counting edit/write tool calls in `session.workflowLogs` for file changes, and total `tool_call` events for tool call count. This data is already in `CoderSessionItem.workflowLogs`.

**Why:** The session list on the issue page should be a compact navigation index, not an embedded viewer. Clicking navigates to the full page.

**Alternatives considered:**
- Keep both navigation and inline expansion — confusing UX, user wouldn't know whether to click to expand or navigate
- Show a preview (first 3 tool calls) inline — still takes too much vertical space

### D5: Auto-scroll behavior on session page

Use a `useEffect` that scrolls a container `div` to the bottom when `rounds` change or `isStreaming` is true. Only auto-scroll when the user is already near the bottom (within 200px threshold) to avoid hijacking scroll when the user is reading earlier content.

**Why:** Standard pattern for chat/streaming UIs. The threshold prevents frustrating scroll jumps.

### D6: Route structure

Add `<Route path="/issue/:number/session/:sessionId" element={<SessionPage />} />` inside the existing `<Route element={<ProjectGuard />}>` block, directly after the `/issue/:number` route. No nested layouts needed — SessionPage renders its own header.

**Why:** React Router v6 matches more specific routes first, so `/issue/86/session/abc-123` won't conflict with `/issue/86`.

## Risks / Trade-offs

- **[Session not found via URL]** → SessionPage shows a 404 state with link back to issue. The session list is fetched from API; if the sessionId doesn't match, show "Session not found" with a link to the issue page.
- **[Large workflowLogs payload]** → `CoderSessionItem.workflowLogs` is embedded in the sessions list response. For sessions with thousands of log entries, this could be slow. Mitigation: the data is already being loaded (it's the same API the inline view uses). No new API call.
- **[Diff display without real diff algorithm]** → Simple line-by-line old/new display won't show intra-line changes. Acceptable trade-off for MVP — users can see what was removed and what was added at the line level.
- **[Tool call interleaving]** → Current `Round.toolCalls` is a flat array appended as tool calls arrive. True interleaving of agent text and tool calls (matching opencode's user→assistant turn model) requires timestamp-based ordering between `agentText` chunks and `toolCalls`. This is not feasible with the current data model where `agentText` is a single concatenated string. Mitigation: for now, show agent text block first, then tool calls below. Interleaving is a future enhancement.

## Migration Plan

1. Add route in `App.tsx` — no breaking change, new route only
2. Create `SessionPage.tsx` — new file, no existing code affected
3. Create `ToolCallCard.tsx` — new file, no existing code affected
4. Modify `SessionHeader.tsx` — change `<button>` to `<Link>`, add `issueNumber` prop
5. Modify `SessionDetail.tsx` — replace full timeline with summary line
6. Modify `SessionList.tsx` — pass `issueNumber` to `SessionHeader`, remove expand/collapse state
7. No rollback needed — the old inline behavior is replaced, not removed behind a flag

## Open Questions

- Should the session page also show the issue's pipeline status timeline (currently on the issue detail page)? **Decision: No — the session page is about a single session's content, not the pipeline. The breadcrumb + issue link provides navigation back to the pipeline view.**
