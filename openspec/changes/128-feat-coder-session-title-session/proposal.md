## Why

48 coder sessions all display as `<mohist-task>\n\n<role>\nYou are implementi...` because there is no `title` field — only a truncated `task_description` from the raw prompt. Each session needs a human-readable name so users can identify sessions at a glance in the activity dashboard and issue detail page.

## What Changes

- Add `title TEXT` column to `coder_session` table (migration + repo)
- Extend `AcpSessionOptions` and `AcpConnectionOptions` with `title?: string`
- Pass `title` at all 7+ caller sites: RalphExecutor (`T-004: Task Name`), PlanStageRunner (`Plan stage`), CheckStageRunner (`Check stage`), CodeCompilesCheck (`Auto-fix: compilation errors`), BuildTestCheck (`Auto-fix: test failures`), SkillService (`Skill: {name}`), ExploreACPService (`Explore: {title}`)
- Include `title` in `coder_session_started` SSE event
- Return `title` from API endpoints (`GET /:number/coder-sessions`, `GET /sessions`)
- Frontend: display `title` with priority over taskDescription-derived fallback (sessionId → executionId parse → stage name → truncated taskDescription)

## Capabilities

### New Capabilities

- `coder-session-title` — session title field: DB schema, repo interface, option types, SSE event payload

### Modified Capabilities

- `coder-session-tracking` — `coder_session` table gains `title` column; `CreateCoderSessionData` includes `title`; SSE `coder_session_started` event carries `title`
- `agent-session-ui` — `CoderSessionItem` type gains `title: string | null`; `SessionHeader` and `SessionCard` display title with fallback priority chain
- `pipeline-session-events` — `AcpSessionOptions` and `AcpConnectionOptions` gain `title?: string` field
- `http-api` — `GET /:number/coder-sessions` and `GET /sessions` responses include `title` field

## Impact

- **DB**: Migration adds nullable `title` column to `coder_session` — backward compatible, no data loss
- **Backend**: `acp-session.ts` (runAcpSession + createAcpConnection), 7 caller files, 2 API route files, repo + migration
- **Frontend**: `types.ts`, `SessionHeader.tsx`, `useCoderSessions.ts`, `useActivityCards.ts`, `SessionCard.tsx`
- **No breaking changes**: `title` is optional; old sessions without title use frontend fallback chain
