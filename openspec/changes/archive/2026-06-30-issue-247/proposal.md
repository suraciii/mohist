## Why

The server models and transmits 11 session-usage fields — token明細 (input, output, total, cache-saved, thought/reasoning), cost, and context-window health — but the UI discards most of them. `cachedReadTokens` and `thoughtTokens` are carried through every read model and rendered by **zero** components; the server-computed `healthStatus` (green/yellow/red) and `contextUsagePercent` are dropped during metadata mapping and then recomputed client-side, redundantly and with risk of drift. There is no single place to see a session's complete consumption profile, and no aggregate view of what an issue cost across its sessions. The data exists; the visibility does not.

## What Changes

- Render `cachedReadTokens` (cache-saved tokens) and `thoughtTokens` (reasoning-model tokens) in the session-page observability bar / header row so the full token明細 is visible.
- Surface a complete **session-level usage summary** on the session page covering every usage field (input/output/total/cached/thought tokens, cost, context window used/size, context-usage %, health status) in one place.
- Carry a usage摘要 (at least total tokens + context %) in the sticky session-title region so usage stays visible while the transcript scrolls.
- Consume the server-provided `healthStatus` and `contextUsagePercent` directly as the source of truth; **remove** the redundant client-side recomputation in `context-health.ts`, the observability bar, and the workflow-sessions panel rows.
- Show an **issue-level usage total** (total tokens + total cost aggregated across the issue's sessions) on the issue page.
- Fix the `useWorkflowRunSessions` SSE `usage.updated` handler to carry `contextUsagePercent` and `healthStatus` so the workflow-sessions panel receives live context-health updates (parity with `useCoderSessions`).
- Replace the `SessionDetail` dead-stub region with a meaningful session-detail display.

## Capabilities

### New Capabilities

_(none — every change extends an existing capability.)_

### Modified Capabilities

- `agent-session-ui`: The session page must surface the complete session usage summary — token明細 including cached and thought tokens, cost, and context health — in one observable place; the observability/header row expands to include cache and thought tokens; the sticky title region carries a usage摘要 that stays visible during transcript scroll.
- `session-health`: The UI consumes the server-provided `healthStatus` classification and `contextUsagePercent` as the source of truth, rather than recomputing context health client-side from the context-window ratio; the client-side reclassification is removed.
- `session-list`: The issue/workflow-run view surfaces total token and cost aggregated across the issue's sessions, and the realtime (SSE) usage feed delivers the complete usage payload (including `contextUsagePercent` and `healthStatus`) so the panel reflects live context health.

## Impact

- **Web (`packages/web`)** — primary surface for all changes:
  - `pages/session/ui/SessionPage.tsx` (`SessionHeader`/observability bar + `buildSessionMetadata`): stop dropping `contextUsagePercent`/`healthStatus`; add cached/thought tokens; add session-level summary; add sticky-title usage摘要; replace `SessionDetail` stub.
  - `widgets/session-health/model/context-health.ts`: remove client-side percent/classification recompute; prefer server values.
  - `widgets/issue-workflow/ui/WorkflowSessionsPanel.tsx`: consume server `contextUsagePercent`/`healthStatus`; render issue-level usage totals.
  - `entities/coder-session/model/useWorkflowRunSessions.ts`: SSE `usage.updated` handler parity fix.
- **Server (`packages/server`)**: No domain change expected — the DTOs already carry all fields. A new issue/workflow-run-scoped usage-aggregation read endpoint **may** be introduced if client-side summation proves insufficient (design decision for `design.md`).
- **APIs**: Existing session/usage read endpoints are unchanged in contract; at most a new aggregation endpoint is added.
