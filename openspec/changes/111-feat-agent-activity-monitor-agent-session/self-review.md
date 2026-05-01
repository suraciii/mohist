## Verdict: PASS

## Summary

All artifacts — proposal, specs (4 files), design, and tasks — are complete, consistent, and ready for implementation. Two issues were found and fixed during review.

## Completeness

### Requirements → Specs coverage

All 10 user acceptance criteria from the issue are covered by specs:

| AC | Spec | Requirement |
|----|------|-------------|
| 1. See 2 active session cards | agent-activity-page/spec.md | Active session card display |
| 2. Card shows issue#, task, duration, last activity | agent-activity-page/spec.md | Active session card display |
| 3. 2-3 activity previews on card | agent-activity-page/spec.md | Activity previews from SSE events |
| 4. Running >30min warning badge | agent-activity-page/spec.md | Anomaly detection badges |
| 5. Idle >5min warning badge | agent-activity-page/spec.md | Anomaly detection badges |
| 6. Click card → session detail page | agent-activity-page/spec.md | Click-through to session detail |
| 7. Session completes → move to Recent | agent-activity-page/spec.md | Recent section + Real-time updates |
| 8. StatusBar counts update real-time | agent-activity-page/spec.md | StatusBar displays global counters |
| 9. Mobile responsive | agent-activity-page/spec.md | Mobile responsive layout |
| 10. All SSE updates without refresh | agent-session-ui/spec.md | SSE events dispatched + Real-time updates |

### Specs → Tasks coverage

All 4 spec files have corresponding tasks:

| Spec | Tasks |
|------|-------|
| agent-activity-page/spec.md (7 requirements) | T-005, T-007, T-008, T-009 |
| agent-session-ui/spec.md (2 requirements) | T-006 |
| http-api/spec.md (1 requirement) | T-001, T-002, T-010 |
| web-ui/spec.md (4 requirements) | T-003, T-004 |

### Edge cases covered

- No sessions exist → empty array from API, empty state in UI
- No workflow_log entries → lastActivityAt is null
- Multiple anomalies on same card → all displayed
- Anomaly resolution → badge removed when condition clears
- Multiple agents streaming simultaneously → RAF throttling
- Server restart stale state → idle >5min badge surfaces quickly

## Consistency

### Proposal ↔ Specs alignment

Proposal lists 4 capabilities. Exactly 4 spec files exist with matching names:
- `agent-activity-page` (new) → `specs/agent-activity-page/spec.md`
- `http-api` (modified) → `specs/http-api/spec.md`
- `web-ui` (modified) → `specs/web-ui/spec.md`
- `agent-session-ui` (modified) → `specs/agent-session-ui/spec.md`

### Design ↔ Specs alignment

| Design Decision | Spec Coverage |
|-----------------|---------------|
| D1: SQL JOIN + subquery | http-api/spec.md — Last activity derived from workflow_log |
| D2: Two-source initial load | agent-session-ui/spec.md — SSE events dispatched |
| D3: useRef+useState pattern | agent-session-ui/spec.md — RAF throttling |
| D4: Client-side anomaly | agent-activity-page/spec.md — Anomaly detection badges |
| D5: RAF throttling | agent-session-ui/spec.md — RAF throttling |
| D6: Extend createAgentRoutes | http-api/spec.md — endpoint spec |
| D7: setInterval duration | agent-activity-page/spec.md — Running duration live update |

### Naming consistency

All references use consistent naming: `GET /api/agent/sessions`, `useAgentSessions`, `useActivityCards`, `ActivityPage`, `StatusBar`, `SessionCard`, `AnomalyBadge`.

## Feasibility

### Dependencies available

- `CoderSessionRepo` exists in `packages/cli/src/db/coder-session-repo.ts` — extended in T-001
- `createAgentRoutes()` exists in `packages/cli/src/api/agent.ts` — extended in T-002
- `onAgentEvent()` from `agent-events.ts` — used by T-006 (same pattern as `useSessionTimeline`)
- `api.ts` request helper — extended in T-003
- `useQueries.ts` React Query pattern — extended in T-003

### No circular dependencies

Dependency graph is a valid DAG:
```
T-001 ──→ T-002 ──→ T-003 ──→ T-006 ──→ T-007 ──→ T-008 ──┐
                                     │                         ↓
T-004 ──→ T-005 ────────────────────┴──────────────────→ T-009
         ↑                                                ↑
         └────────────────────────────────────────────────┘
T-002 ──→ T-010
```

### Task granularity

Each task produces one coherent output file (or a pair of tightly coupled files like api.ts + useQueries.ts). Integration task T-009 is appropriately the final task.

## Dependency Validation

| Task | dependsOn | All refs exist? | All lower priority? |
|------|-----------|-----------------|---------------------|
| T-001 (p1) | [] | yes | n/a |
| T-002 (p2) | [T-001] | yes | p1 < p2 |
| T-003 (p3) | [T-002] | yes | p2 < p3 |
| T-004 (p4) | [] | yes | n/a (parallel root) |
| T-005 (p5) | [T-004] | yes | p4 < p5 |
| T-006 (p6) | [T-003] | yes | p3 < p6 |
| T-007 (p7) | [T-006] | yes | p6 < p7 |
| T-008 (p8) | [T-007] | yes | p7 < p8 |
| T-009 (p9) | [T-004, T-005, T-006, T-007, T-008] | yes | all < p9 |
| T-010 (p10) | [T-002] | yes | p2 < p10 |

No cycles. No forward dependencies.

Note: T-004 has no dependsOn despite being priority 4. This is intentional — it creates a placeholder page and navigation entries that are independent of all backend work. It forms a parallel root alongside T-001.

## Issues Found and Fixed

### Issue 1: T-009 missing dependency on T-004 (Fixed)

T-009 replaces the placeholder ActivityPage created by T-004 but did not declare it as a dependency. Added `"T-004"` to T-009's dependsOn array.

**File:** tasks.json line 212 — changed from `["T-005", "T-006", "T-007", "T-008"]` to `["T-004", "T-005", "T-006", "T-007", "T-008"]`

### Issue 2: Design D6 referenced IssueService instead of ProjectService (Fixed)

Design decision D6 and migration step 2 mentioned injecting `IssueService` but the SQL query needs `projectId` from `ProjectService.getCurrentId()`. Corrected both references to `ProjectService`.

**File:** design.md line 114 — changed `IssueService` to `ProjectService` with clarification about `getCurrentId()`
**File:** design.md line 134 — changed migration step 2 from `IssueService` to `ProjectService`
