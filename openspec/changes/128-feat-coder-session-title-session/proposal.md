## Why

Coder sessions display `task_description` (raw prompt prefix) in the UI, making all 48 sessions show identical `<mohist-task>\n\n<role>\nYou are implementi...` — completely unreadable. Each session needs a human-readable title set by the caller at creation time, so users can distinguish sessions at a glance.

## What Changes

- Add `title TEXT` column to `coder_session` table
- Extend `AcpSessionOptions` and `AcpConnectionOptions` with `title?: string`
- 7 callers pass meaningful titles (e.g., `"T-004: Create Plan"`, `"Plan stage"`, `"Auto-fix: compilation errors"`)
- `coder_session_started` SSE event carries `title`
- API endpoints return `title` field for coder sessions
- Frontend uses `title` as primary display label with fallback chain (title → executionId task parse → stage name → taskDescription prefix)

## Capabilities

### New Capabilities

- `coder-session-title`: Session titles set at creation time by callers, stored in DB, returned via API, displayed in frontend with fallback chain.

### Modified Capabilities

- `coder-session-tracking`: `CreateCoderSessionData` interface and insert logic extended to persist `title`.
- `pipeline-session-events`: `AcpSessionOptions` and `AcpConnectionOptions` gain `title` field; `coder_session_started` SSE event includes `title`.
- `agent-session-ui`: Frontend `CoderSessionItem` type gains `title`; `SessionHeader` and active session cards use title as primary label.

## Impact

- **DB**: New nullable `title` column in `coder_session` table (migration)
- **Backend**: `acp-session.ts` (2 creation sites), 7 caller files, 2 API route files, `coder-session-repo.ts`, `migrations.ts`
- **Frontend**: `types.ts`, `SessionHeader.tsx`, `useCoderSessions.ts`, `useActivityCards.ts`, `SessionCard.tsx`
- **Backward compatible**: No breaking changes; `title` is nullable, frontend falls back to existing heuristics for old data
