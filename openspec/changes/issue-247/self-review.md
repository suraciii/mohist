# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: T-003 implements two requirements from `agent-session-ui/spec.md` — the "Session-level complete usage summary" and the "Token detail in the session observability bar and header row" requirement (its description adds cached/thought tokens to the observability bar and its acceptance criteria cover the cached/thought-observable + inapplicable-omission scenarios). However, the `spec` field only referenced the first, so the Token detail requirement had no traceable task pointer despite being implemented. Updated `tasks.json` T-003 `spec` to list both requirement anchors.
  Verification: `python3 -c "import json; json.load(open('openspec/changes/issue-247/tasks.json'))"` reports valid JSON; the spec string now references both requirements; T-003 description/acceptanceCriteria already covered the Token detail scenarios, so no task content changed.

## Blocking Items

_(none)_

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design Open Questions flags `useCoderSessions` lacks a `context_health_update` handler (only `useWorkflowRunSessions` and `useSessionTimeline` receive it). Any surface fed solely by `useCoderSessions` will not get live context-health until refetch. The `session-list` spec scopes the realtime fix to `useWorkflowRunSessions`, so this is correctly out of scope here.
  SuggestedAction: Add `context_health_update` + full `usage.updated` parity to `useCoderSessions` in a follow-up change if a non-session-page consumer is identified.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: Decision 5 implements issue-level aggregation client-side by summing the workflow-run sessions list. This is correct for the current small per-issue session count. A server-side `GET /workflow-runs/{id}/usage` endpoint is the documented escalation path if the session list grows large enough that fetching all rows just to sum them becomes wasteful.
  SuggestedAction: Introduce the aggregation endpoint when the session count per issue makes client-side summation wasteful.
  Status: follow-up

---

### Review Notes

Verified design.md codebase claims against source — all accurate:
- `buildSessionMetadata` (`SessionPage.tsx:25-79`) maps every usage field except `contextUsagePercent`/`healthStatus` — confirmed.
- `useWorkflowRunSessions.ts:87` has `usage.updated` but no `context_health_update` handler; `useCoderSessions.ts:150-151` applies the two derived fields; `useSessionTimeline.ts:691` handles `context_health_update`.
- `context-health.ts` exposes `classifyContextHealth`, `resolveContextUsagePercent`, `resolveContextUsage` recompute helpers.
- `SessionDetail` does not exist in source (repo-wide find returns nothing) — Decision 3 correctly reframes the stale issue reference as a net-new summary region.
- `SessionPage.sticky.test.tsx:328-340` asserts no `[class*="sticky"]` outside the scroll container — Decision 4 reconciles it correctly.
- `WorkflowSessionsPanel.tsx:261-269` already sums `totalTokens` + `summarizeCost`; `summarizePeakContext` exists at line 120.
- `types.ts:30-31` carries `cachedReadTokens`/`thoughtTokens`; `types.ts:36-37` carries `contextUsagePercent`/`healthStatus`.

Coverage matrix (issue acceptance criteria → spec → task):
1. cached/thought tokens in observability bar + header row → `agent-session-ui` Token detail → T-003
2. healthStatus consumed directly, no client recompute → `session-health` → T-002
3. session-level usage summary (all fields) → `agent-session-ui` Session-level summary → T-003
4. StickySessionTitle total tokens + context% → `agent-session-ui` Sticky → T-004
5. Issue page total tokens + total cost → `session-list` Issue-level aggregation → T-005
6. Fix useWorkflowRunSessions SSE → `session-list` Realtime feed → T-001
7. Replace SessionDetail dead-stub → `agent-session-ui` Session detail region scenario → T-003 (Decision 3 reframes)

Dependency check: T-001/T-002 have no deps; T-003→T-002; T-004→T-002,T-003; T-005→T-002. All `dependsOn` point to existing IDs with strictly lower priority. No cycles.

Task granularity: all 5 tasks are complete feature slices with embedded vitest acceptance criteria — no separate "test" tasks, no "define interface"/"register DI"/"create file" micro-tasks, no install/start/stop splits.

<promise>PASS</promise>
