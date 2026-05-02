## Why

All coder sessions display as truncated raw prompt text (e.g. `<mohist-task>\n\n<role>\nYou are implementi...`), making the session list and activity cards unreadable when managing multiple concurrent agents. The `coder_session` table has no `title` field — each caller already has enough context to name the session at creation time.

## What Changes

- Add `title TEXT` column to `coder_session` table (DB migration)
- Add `title?: string` to `AcpSessionOptions` and `AcpConnectionOptions` interfaces; pass through to coder_session insert in both `runAcpSession` and `createAcpConnection`
- All 8 callers supply a human-readable title at session creation (RalphExecutor, PlanStageRunner, CheckStageRunner, CodeCompilesCheck, BuildTestCheck, SkillService, ExploreACPService, ConflictResolution)
- SSE `coder_session_started` event carries `title`
- API endpoints (`GET /:number/coder-sessions`, `GET /api/agent/sessions`) return `title` field
- Frontend session display prioritizes `title` over executionId-derived taskId, stage name, or truncated taskDescription

## Capabilities

### New Capabilities

- `coder-session-title`: Named coder sessions — DB field, caller-provided titles, SSE transport, API exposure, and frontend display priority chain (title > taskId > stage > taskDescription fallback).

### Modified Capabilities

- `coder-session-tracking`: `coder_session` table gains `title` column; SSE `coder_session_started` event payload gains `title` field
- `agent-activity-page`: Session cards display `title` as primary label instead of truncated taskDescription
- `session-list-ui`: SessionHeader uses `title` in the session display priority chain

## Impact

- **DB**: New nullable `title` column on `coder_session` table via migration
- **Backend**: `acp-session.ts` (2 insert sites), `coder-session-repo.ts`, `migrations.ts`
- **Callers** (8 files): `ralph-executor.ts`, `plan-stage-runner.ts`, `check-stage-runner.ts`, `code-compiles-check.ts`, `build-test-check.ts`, `skill-service.ts`, `explore-acp-service.ts`, `conflict-resolution.ts`
- **API**: `issues.ts` and `agent.ts` route handlers return `title`
- **Frontend**: `types.ts`, `SessionHeader.tsx`, `useCoderSessions.ts`, `useActivityCards.ts`, `SessionCard.tsx`
- **Backward compatibility**: No breaking changes — `title` is nullable; old sessions without title use existing fallback display logic
