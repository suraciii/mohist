## Context

The WebUI already has per-issue session monitoring via `SessionTimeline` + `useSessionTimeline` (subscribes to SSE via `onAgentEvent`). The issue detail page (`/issue/:number`) shows one agent at a time. There is no cross-issue view.

Backend already has:
- `coder_session` table with status, model, stage, task_description
- `workflow_log` table with all ACP events per session
- `GET /api/agent/status` returning in-memory `activeAgents[]` and `waitingQuestions[]`
- `CoderSessionRepo` with `findByIssueId` but no cross-issue query
- `WorkflowLogRepo` with `findBySessionId` but no "latest per session" query

Frontend already has:
- `useSSE` hook + `agent-events.ts` EventTarget bus for SSE dispatch
- `useQueries.ts` with React Query pattern
- `api.ts` with `request<T>()` helper
- `Header` and `MobileBottomNav` for navigation

## Goals / Non-Goals

**Goals:**
- One new backend endpoint (`GET /api/agent/sessions`) joining coder_session + issues + workflow_log
- One new frontend page (`/activity`) consuming that endpoint + SSE events for real-time card updates
- Navigation entries in Header and MobileBottomNav
- Anomaly detection badges computed client-side from card state

**Non-Goals:**
- Split-pane layout (list + live conversation)
- Stop/Recover action buttons on cards
- Token cost tracking
- Custom alert rule configuration
- New SSE event types (reuse existing ones)
- New database tables or migrations

## Decisions

### D1: Backend query — SQL JOIN with correlated subquery for lastActivityAt

Use a single SQL query joining `coder_session` + `issues` and a correlated subquery on `workflow_log` to get `lastActivityAt`. No N+1 queries.

```sql
SELECT cs.*, i.number as issue_number, i.title as issue_title, i.stage as issue_stage,
  (SELECT MAX(wl.created_at) FROM workflow_log wl WHERE wl.session_id = cs.acp_session_id) as last_activity_at
FROM coder_session cs
JOIN issues i ON cs.issue_id = i.id
WHERE i.project_id = ?
  AND (? IS NULL OR cs.status = ?)
ORDER BY cs.created_at DESC
LIMIT ?
```

Add a new method `CoderSessionRepo.findAllWithIssueInfo(projectId, status?, limit?)` that returns the joined result. No new repo — extend the existing one.

**Alternatives considered:**
- Separate API call for each session's last activity → N+1 problem, rejected
- Denormalize lastActivityAt into coder_session table → requires migration and write-path changes, rejected
- Application-level join in TypeScript → multiple queries, more complex, rejected

### D2: Activity page state — client-side aggregation from two sources

The ActivityPage loads initial data from `GET /api/agent/sessions` (historical) and `GET /api/agent/status` (in-memory running/waiting state). SSE events then mutate card state in-place. No periodic polling.

Initial load:
1. `useAgentSessions()` → fetches all sessions (for Active + Recent sections)
2. `useAgentStatus()` → fetches activeAgents, waitingQuestions, maxConcurrentAgents (for StatusBar + Waiting section)

Real-time updates via `onAgentEvent()`:
- `coder_session_started` → add card to Active map
- `coder_session_completed` → move card from Active to Recent
- `coder_text_chunk` / `coder_tool_call` → update activity previews on matching card
- `ralph_task_update` → update progress bar on matching card
- `agent_paused` → add card to Waiting
- `question_asked` → add card to Waiting
- `question_answered` → remove from Waiting

**Alternatives considered:**
- Full React Query cache invalidation on every SSE event → excessive refetching, rejected
- Server-sent card state (new SSE event type) → adds backend complexity, rejected

### D3: Card state managed in a useRef + useState pattern

Use a `useRef<SessionCardMap>` for card state mutated by SSE events, with a `useState` counter to trigger re-renders at throttled intervals. This avoids React state overhead on every SSE event while keeping the UI responsive.

The `SessionCardMap` is keyed by `issueNumber` (string) since all SSE events use `issueId: String(issueNumber)`.

```
SessionCard {
  issueNumber, issueTitle, issueStage,
  sessionId, status, model, taskDescription,
  createdAt, completedAt, lastActivityAt,
  activityPreviews: string[] (max 3),
  taskProgress: { completed, total } | null,
}
```

### D4: Anomaly detection — pure client-side computation from card timestamps

Anomaly badges are computed from card data already in state:
- `running > 30min`: `Date.now() - card.createdAt > 30 * 60_000`
- `idle > 5min`: `Date.now() - card.lastActivityAt > 5 * 60_000` (only if lastActivityAt exists)
- `unanswered > 10min`: `Date.now() - waitingCard.questionAskedAt > 10 * 60_000`

A `setInterval` every 30 seconds re-evaluates anomaly conditions. No backend involvement.

**Alternatives considered:**
- Backend anomaly detection with dedicated endpoint → over-engineering for rule-based checks, rejected
- Background worker computing anomalies → unnecessary complexity, rejected

### D5: RAF throttling for coder_text_chunk events

Reuse the same pattern as `useSessionTimeline`: buffer `coder_text_chunk` events in a ref, flush every 100ms via `requestAnimationFrame`. This prevents UI jank when multiple agents stream text simultaneously.

### D6: Activity route added to existing agent route file

Add the `GET /api/agent/sessions` handler to the existing `createAgentRoutes()` in `packages/cli/src/api/agent.ts`. Inject `CoderSessionRepo` and `ProjectService` as additional parameters to the factory function. `ProjectService.getCurrentId()` provides the `projectId` for the SQL WHERE clause.

**Alternatives considered:**
- New route file `api/agent-sessions.ts` → too granular for one endpoint, rejected
- Add to `api/issues.ts` → semantically wrong (this is an agent-level cross-issue query), rejected

### D7: Running duration timer — client-side setInterval

Active cards show elapsed time updated every second. Use a single `setInterval(1000)` at the page level that increments a render counter. Cards compute display string from `Date.now() - card.createdAt`.

## Risks / Trade-offs

- **[SQL subquery perf on large workflow_log]** → Mitigation: `workflow_log` has index on `(issue_id, created_at)` but not on `session_id`. Add `LIMIT` to subquery (`SELECT created_at FROM workflow_log WHERE session_id = ? ORDER BY created_at DESC LIMIT 1`). For production, consider adding an index on `workflow_log(session_id, created_at)` if query is slow.
- **[SSE event volume with 8 concurrent agents]** → Mitigation: RAF throttling for text chunks; activity preview buffer capped at 3 entries per card; only matching cards are updated (filter by issueId).
- **[Stale in-memory state after server restart]** → `GET /api/agent/status` only knows about currently running agents (in-memory). After restart, sessions marked `running` in DB but not in `activeAgents` will show as "active" from the sessions API but won't receive SSE events. Mitigation: the sessions API returns DB-level status, so these will appear in the Active section with a stale `lastActivityAt`, triggering the idle >5min anomaly badge quickly.
- **[Waiting section data gap]** → `waitingQuestions` from `GET /api/agent/status` only tracks currently blocked in-memory agents. Historical pending questions from DB are not included. Mitigation: acceptable for MVP; the Waiting section only shows real-time state.

## Migration Plan

1. Add `findAllWithIssueInfo()` to `CoderSessionRepo` (no schema changes)
2. Extend `createAgentRoutes()` signature to accept `CoderSessionRepo` and `ProjectService`
3. Update `server/index.ts` to pass new dependencies when mounting agent routes
4. Add `getAgentSessions` to frontend `api.ts`
5. Add `useAgentSessions` to `useQueries.ts`
6. Create `ActivityPage` component with `StatusBar`, `SessionCard`, `WaitingCard`, `RecentCard`
7. Create `useActivityCards` hook managing SSE subscriptions and card state
8. Add `/activity` route in `App.tsx`
9. Add Activity link to `Header.tsx` and `MobileBottomNav.tsx`

Rollback: Remove the route, page component, and nav entries. Backend endpoint can remain (unused endpoints are harmless). No database changes to revert.

## Open Questions

None.
