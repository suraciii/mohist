## Why

Sessions are trapped inside a 400px collapsed panel on the Issue Detail page — no direct URL, no diff view, no conversational flow. Users cannot meaningfully review what an agent did during a session, making it impossible to understand code changes or debug agent behavior.

## What Changes

- Add new route `/issue/:number/session/:sessionId` for a dedicated session detail page
- Create `SessionPage` component with full-width, opencode-style conversational timeline (Round → agent text + inline tool calls)
- Refactor `RoundSection` into a dialogue-style view: agent text as prose, edit tool calls render inline diff, bash shows terminal output, read/glob/grep fold into compact summaries
- Convert `SessionHeader` in `SessionList` from expand/collapse toggle to a `<Link>` that navigates to the session page
- Simplify inline `SessionDetail` to a summary-only view (file change count, key operations) instead of the full timeline
- Add breadcrumb navigation back to the parent issue page

## Capabilities

### New Capabilities

- `session-page` — Full-page session detail view at `/issue/:number/session/:sessionId`, rendering rounds as conversation cards with inline tool call details (edit diffs, bash output, collapsed summaries for low-value tools)

### Modified Capabilities

- `session-timeline-ui` — `SessionHeader` becomes a navigation link; inline `SessionDetail` reduces to summary-only; `RoundSection` gains dialogue-style rendering with tool-specific display (diff for edit, terminal for bash, collapsed for read/glob/grep)
- `web-ui` — New route `/issue/:number/session/:sessionId` added to router

## Impact

- **Frontend routing**: `App.tsx` gains a new route under the `ProjectGuard`
- **Components**: New `SessionPage.tsx`; modified `SessionList.tsx`, `SessionHeader.tsx`, `SessionDetail.tsx`, `SessionTimeline.tsx` (RoundSection refactor)
- **Hooks**: `useSessionTimeline` used in new standalone context (already supports `session` object mode)
- **No backend changes**: All data already available via existing `coder_sessions` API and workflow_log API
