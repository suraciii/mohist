## Context

`workflow_log` table currently receives ~97% session chunk data (agent_thought_chunk, agent_message_chunk, tool_call, tool_call_update) alongside workflow-level events (build/check/task/acp_session lifecycle). Two code paths write session data:

1. **Single-shot** (`runAcpSession`, `acp-session.ts:246-253`) — unconditionally writes every `sessionUpdate` event type to `workflow_log`
2. **Multi-round** (`createAcpConnection`, `acp-session.ts:663-670`) — same unconditional write

Two API paths read session data:
1. `GET /api/issues/:number/coder-sessions` — calls `workflowLogRepo.findBySessionId()` per session, embeds as `workflowLogs`
2. `GET /api/issues/:number/logs` — calls `workflowLogRepo.findByIssueId()`, returns all entries

Frontend consumers:
- `useIssueTimeline.ts:263` — fetches via `api.getWorkflowLogs()` but never uses result (buildTimeline ignores `_logs`)
- `useSessionTimeline.ts:231` — fetches via `api.getWorkflowLogs()` when no `session` prop (Path B fallback)
- `useCoderSessions.ts:10` — fetches sessions with embedded logs via `api.getCoderSessions()`

Current schema version: **18**. New table will be version **19**.

## Goals / Non-Goals

**Goals:**
- Separate session stream events into `session_stream_log` table
- Keep `workflow_log` for workflow-level events only
- `coder-sessions` API serves session logs from `session_stream_log`
- Remove unused `api.getWorkflowLogs()` call from `useIssueTimeline`
- No historical data migration

**Non-Goals:**
- Session pre-aggregation fields on `coder_session` (tool_calls_count, files_changed_count) — deferred to follow-up
- Data retention/cleanup policy for `session_stream_log` — deferred
- Migrating historical chunk data from `workflow_log` to `session_stream_log`
- Changing frontend `WorkflowLogItem` type or round reconstruction logic

## Decisions

### D1: New file `session-stream-log-repo.ts` mirrors `workflow-log-repo.ts` pattern

Create `packages/cli/src/db/session-stream-log-repo.ts` with identical structure to `WorkflowLogRepo` — same `insert`, `findBySessionId`, `findByIssueId` methods. The repo is structurally identical because the table schema mirrors `workflow_log` minus the nullable `session_id` (now NOT NULL).

**Alternatives considered:**
- Extending `WorkflowLogRepo` with a `table` parameter — rejected: different repos should have different identities; `session_id` is required in the new table
- Single generic `LogRepo<Table>` — rejected: over-engineering for two tables

### D2: `AcpSessionOptions` gains `sessionStreamLogRepo` field alongside existing `workflowLogRepo`

Both repos are optional (consistent with existing pattern). The `sessionUpdate` handler checks event type against a constant set `SESSION_STREAM_EVENT_TYPES` and routes accordingly. This keeps the routing logic centralized in one place per code path.

```typescript
const SESSION_STREAM_EVENT_TYPES = new Set([
  'agent_thought_chunk',
  'agent_message_chunk',
  'tool_call',
  'tool_call_update',
  'user_message_chunk',
]);
```

At `acp-session.ts:246-253` (single-shot) and `acp-session.ts:663-670` (multi-round), replace:
```
if (workflowLogRepo) { workflowLogRepo.insert(...) }
```
with:
```
if (SESSION_STREAM_EVENT_TYPES.has(eventType)) {
  sessionStreamLogRepo?.insert(issueId, sessionId, eventType, data);
} else {
  workflowLogRepo?.insert(issueId, sessionId, eventType, data);
}
```

The `writeSessionLog` helper (lifecycle events like `acp_session_start`, `acp_session_timeout`, etc.) continues writing to `workflowLogRepo` unchanged — these events are never in `SESSION_STREAM_EVENT_TYPES`.

**Alternatives considered:**
- Passing a single repo that auto-routes — rejected: implicit routing is harder to debug; explicit dual-repo makes data flow visible at call sites
- Moving session stream writes entirely into a new function — rejected: unnecessary indirection; the routing check is one `if` statement

### D3: `coder-sessions` API reads from `session_stream_log`, falls back to `workflow_log` for old data

At `api/issues.ts:1650`, change:
```
const logs = workflowLogRepo.findBySessionId(session.acpSessionId);
```
to query `sessionStreamLogRepo.findBySessionId()` first. If empty (old sessions predating the migration), fall back to `workflowLogRepo.findBySessionId()` and filter to only session stream event types. This ensures old session detail pages still render correctly.

**Alternatives considered:**
- No fallback, old sessions show empty rounds — rejected: breaks existing user experience
- Migrate historical data at upgrade time — rejected: avoid large data migration risk; per-query fallback is cheap

### D4: `useSessionTimeline` Path B fetches from `coder-sessions` API instead of `/logs`

Currently when no `session` prop is provided, `useSessionTimeline.ts:229-233` fetches all workflow logs via `api.getWorkflowLogs()` and reconstructs rounds. After this change, Path B should be removed or converted to use `api.getCoderSessions()` — but this is a frontend behavioral change that needs careful handling.

For this iteration: keep Path B as-is. The `/logs` API still returns workflow_log data (which no longer includes chunks). Path B will produce empty rounds for new sessions. This is acceptable because Path B is only used for the "no session selected" view, and the primary session detail view uses Path A (`session.workflowLogs` from coder-sessions API).

**Alternatives considered:**
- Rewrite Path B to fetch from coder-sessions — deferred: Path B is a secondary view; the primary flow (Path A) is what matters

### D5: Remove `api.getWorkflowLogs()` from `useIssueTimeline` only

`useIssueTimeline.ts:259-269` fetches workflow logs into `logsRef` but `buildTimeline()` ignores the `_logs` parameter (line 135). Remove the `useEffect` and the `logsFetchedRef` ref. This is a pure cleanup — no behavior change since the data was unused.

Do NOT remove `api.getWorkflowLogs()` from `useSessionTimeline` yet (see D4).

### D6: Schema migration version 19

Add `migrateToVersion19` in `migrations.ts`:
```sql
CREATE TABLE IF NOT EXISTS session_stream_log (
  id          TEXT PRIMARY KEY,
  session_id  TEXT NOT NULL,
  issue_id    TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  event_type  TEXT NOT NULL,
  data        TEXT NOT NULL DEFAULT '{}',
  created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_session_stream_log_session ON session_stream_log(session_id, created_at);
CREATE INDEX IF NOT EXISTS idx_session_stream_log_issue ON session_stream_log(issue_id, created_at);
```

Register in the migration switch at version 19. Update `SCHEMA_VERSION` constant to 19.

### D7: `sessionStreamLogRepo` plumbed through same path as `workflowLogRepo`

Follow the existing dependency injection path:
1. `state-manager.ts` — instantiate `SessionStreamLogRepo`, expose `getSessionStreamLogRepo()`
2. `server/index.ts` — retrieve from state manager, pass to `AgentRunnerService` and `createIssueRoutes`
3. `workflow-engine.ts` (`AcpOptions`) — add `sessionStreamLogRepo?` field
4. `stage-context.ts` — add to context type
5. `acp-session.ts` — **both** `AcpSessionOptions` (single-shot, line 35) **and** `AcpConnectionOptions` (multi-round, line 463) need `sessionStreamLogRepo?` field. These are two separate interfaces.
6. `conflict-resolution.ts` — `ConflictResolutionDeps` needs `sessionStreamLogRepo` field
7. All callers that currently pass `workflowLogRepo` also pass `sessionStreamLogRepo`

Note: `event-bus.ts` `emitPersistent` also accepts `workflowLogRepo` in its opts parameter but is currently dead code (never called). Skip for now.

The propagation is mechanical but touches ~10 files.

## Risks / Trade-offs

- **[Old session detail pages may render incomplete if fallback fails]** → Mitigation: D3 fallback queries `workflow_log` for sessions with no `session_stream_log` entries
- **[Dual-repo plumbing is verbose, ~10 files touched]** → Mitigation: Mechanical changes, no logic complexity; follow existing `workflowLogRepo` pattern exactly
- **[Path B in `useSessionTimeline` will show empty rounds for new sessions]** → Mitigation: Path B is secondary view; primary session detail uses Path A which reads from `session_stream_log`. Document as known limitation.
- **[No backward-compatible `/logs` API for session chunks]** → Mitigation: `GET /api/issues/:number/logs` was already returning too much data; consumers should use `coder-sessions` API

## Migration Plan

1. Deploy with new `session_stream_log` table (schema v19)
2. New session stream events immediately write to new table
3. `workflow_log` stops receiving chunk data — table growth drops ~97%
4. Old data in `workflow_log` is untouched — old sessions still viewable via D3 fallback
5. No rollback needed — if issues arise, the `session_stream_log` writes can be disabled by not passing the repo, reverting to `workflow_log` behavior

## Open Questions

None — design is fully specified.
