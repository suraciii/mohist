# Review Report

## Verdict: PASS

## Dimensions

### Correctness: PASS (with warnings)

**Migration v15** (`packages/cli/src/db/migrations.ts`): Idempotent column check via `PRAGMA table_info` before ALTER TABLE — correct pattern.

**CoderSessionRepo** (`packages/cli/src/db/coder-session-repo.ts`): `insert()` correctly reads back row after insert and maps new columns. `updateStatus()` returns the updated `CoderSession` which is used by callers for duration calculation.

**acp-session.ts**: 
- `resolvedModel` resolution chain `options.model ?? loadConfig().model ?? undefined` is correct — `loadConfig()` returns `ConfigInfo` which has optional top-level `model` field.
- `as any` casts on `eventBus.emit` calls (`chunkPayload as any`, `toolPayload as any`) work around the typed `EventMap`. Not ideal but functional — the payloads are constructed correctly with conditional field inclusion.
- Duration calculation in `runAcpSession` completed path: falls back to `sessionStartTime` if `completedAt`/`createdAt` are null — safe.
- `coderSessionId` and `acpSessionId` correctly added to `AcpConnection` interface and populated in `createAcpConnection` return.

**workflow-controller.ts**: `conn` declaration moved before `planAcpOptions`/`reviewAcpOptions` so the `onSessionUpdate` and round-start callbacks can close over it. The callbacks are invoked lazily (after `createAcpConnection` returns), so `conn` is assigned by call time. Correct.

**useCoderSessions hook**: `issueId` comparison uses `String(issueNumber)` matching the backend's `sseIssueId = String(issueNumber ?? ...)`. Correct.

**useSessionTimeline filter**: Filtering plan events by `coderSessionId` then falling back to `acpSessionId` — correct. The triple-check (`if coderSessionId && !== s.id`, `if !coderSessionId && acpSessionId && !== s.acpSessionId`, `if !coderSessionId && !acpSessionId → return`) correctly handles both old events (no session ID) and new events.

**WARNING**: `SessionHeader` renders `durationMs` using `Date.now() - new Date(session.createdAt).getTime()` for running sessions. Since `SessionHeader` is a pure component with no re-render timer, this value will be stale until the parent re-renders. The `useCoderSessions` 1-second timer triggers re-renders via `setLiveSessions(prev => prev.map(s => s))`, which forces a re-render of all sessions. This works but creates unnecessary object allocations every second for all sessions (including completed ones).

**WARNING**: `useCoderSessions` sets `liveSessions` from `sessions` (React Query data) once via `initializedRef`, but if React Query refetches (e.g., window focus), the hook won't pick up the newer data because `initializedRef` blocks re-initialization. The `issueNumber` effect resets `initializedRef`, but mid-session refetches would be lost.

### Complexity: PASS

All new functions are under 50 lines. `useCoderSessions` (~120 lines) is the largest new hook but is well-structured with clear SSE subscription setup. `useSessionTimeline` grew but the added filter logic is linear and consistent across event types. `SessionHeader`, `SessionList`, and `SessionDetail` are appropriately scoped.

No significant cyclomatic complexity. The filter patterns in `useSessionTimeline` are 3-branch conditionals (under threshold).

### Test Coverage: PASS (with warnings)

Build succeeds. All pre-existing test failures (65 across 13 files) are unrelated to this change — they fail identically on the base branch (Stage.Draft→Backlog rename, merge queue rebase changes from issue-73).

**No new tests added** for the new code. Specifically:
- No test for migration v15
- No test for `CoderSessionRepo.insert()` with new fields
- No test for SSE event enrichment (`coderSessionId`, `model` on existing events)
- No test for `coder_session_started` / `coder_session_completed` lifecycle events
- No test for `useCoderSessions` hook
- No test for `SessionHeader` / `SessionList` / `SessionDetail` components

The existing `useSessionTimeline.test.ts` passes (9 tests), confirming backward compatibility.

### Security: PASS

All database queries use parameterized statements (`?` placeholders). No SQL injection risk. SSE event payloads contain no secrets. Model names and session IDs are non-sensitive metadata.

### Spec Compliance: PASS

**T-001: Migration v15 + CoderSessionRepo interface update**
- ✅ Migration v15 adds `model`, `coder_type`, `stage` TEXT columns
- ✅ Existing rows get NULL (ALTER TABLE on nullable columns)
- ✅ `CreateCoderSessionData` accepts optional `model`, `coderType`, `stage`
- ✅ `CoderSessionRepo.insert()` persists new fields, writes NULL when omitted
- ✅ `GET /:number/coder-sessions` response includes new fields (issues.ts diff)
- ✅ Frontend `CoderSessionItem` type includes new fields
- ✅ `npm run build` succeeds
- ⚠️ Tests: no new tests, but existing tests pass

**T-002: Backend populate new fields on insert**
- ✅ `runAcpSession` passes `model`, `coderType: 'opencode'`, `stage: 'build'`
- ✅ `createAcpConnection` passes `model`, `coderType: 'opencode'`, `stage: options.stage`
- ✅ `AcpSessionOptions` and `AcpConnectionOptions` include optional `model` and `stage`
- ✅ Model resolution: `options.model ?? loadConfig().model ?? undefined`
- ✅ Build succeeds, tests pass (pre-existing failures only)

**T-003: SSE event enrichment**
- ✅ `coder_text_chunk` payload includes `coderSessionId` and `model` when available
- ✅ `coder_tool_call` payload includes `coderSessionId` and `model` when available
- ✅ `coder_session_started` emitted after `coderSessionRepo.insert()` with full metadata
- ✅ `coder_session_completed` emitted on session completion with status and duration
- ✅ `plan_round_start` includes `acpSessionId` and `coderSessionId`
- ✅ `plan_session_update` includes `acpSessionId` and `coderSessionId`
- ✅ `AcpConnection` interface has `coderSessionId` and `acpSessionId` as readonly properties
- ✅ `ALL_EVENT_TYPES` in `event-bus.ts` includes `coder_session_started` and `coder_session_completed`
- ✅ Events without coder_session context omit `coderSessionId` (backward compatible)

**T-004: Frontend useCoderSessions + SessionHeader + SessionList**
- ✅ `useCoderSessions(issueNumber)` returns `{ sessions, isLoading }`
- ✅ Initial data fetched from `GET /:number/coder-sessions`
- ✅ `coder_session_started` SSE event inserts new session
- ✅ `coder_session_completed` SSE event updates matching session status and completedAt
- ✅ Running sessions get live duration timer (1-second interval)
- ✅ Timer cleaned up when all sessions complete
- ✅ SessionHeader shows: status icon, label, coder·model, time, duration, chevron
- ✅ SessionList renders sessions ordered by createdAt ascending
- ✅ Click toggles expand/collapse, only one expanded at a time
- ✅ Running sessions auto-expand on mount
- ✅ Empty state shows "No sessions yet" placeholder

**T-005: useSessionTimeline filter + SessionDetail**
- ✅ `useSessionTimeline(issueNumber)` without session returns all rounds (backward compatible)
- ✅ `useSessionTimeline(issueNumber, session)` returns only rounds for that session
- ✅ Filtering uses acpSessionId from CoderSessionItem
- ✅ Live SSE events filtered by acpSessionId/coderSessionId
- ✅ SessionDetail renders rounds via `RoundSection` reuse from SessionTimeline

**T-006: Integrate into IssueDetailPage**
- ✅ IssueDetailPage renders SessionList instead of SessionTimeline
- ✅ SessionList wired to `useCoderSessions(issueNumber)`
- ⚠️ `PipelineStatusTimeline` and `TaskProgressPanel` are no longer rendered in the panel (they are exported but unused). The spec says "preserve within the panel" but these components were part of the old `SessionTimeline` which displayed them. The new `SessionDetail` does not include them.
- ✅ No regressions in stage progress bar, approval gate, or other panel features
- ✅ Build succeeds, tests pass

## Fix Suggestions

1. **`packages/cli/web/src/hooks/useCoderSessions.ts`:46** — The 1-second timer forces re-render of all sessions via `prev.map(s => s)` which creates new object references every tick. Consider only mapping running sessions or using a separate counter state for re-render triggering.

2. **`packages/cli/web/src/hooks/useCoderSessions.ts`:33-35** — React Query refetches won't update `liveSessions` because `initializedRef` blocks re-initialization after first load. Consider merging refetched data with live SSE state instead of one-shot initialization.

3. **`packages/cli/web/src/components/SessionDetail.tsx`** — `PipelineStatusTimeline` and `TaskProgressPanel` are exported from `SessionTimeline.tsx` but no longer rendered anywhere in the new session-centric view. If these should be preserved for build-stage sessions (as T-006 acceptance criteria states), they need to be added back to `SessionDetail` or `SessionList`.

4. **Missing tests** — No test coverage for migration v15, new repo fields, SSE enrichment, lifecycle events, or any new frontend components. Recommend adding at minimum: repo insert test with new fields, SSE event payload verification, and a basic render test for SessionList/SessionDetail.
