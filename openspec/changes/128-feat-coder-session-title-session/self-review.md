## Self-Review: Issue #128 — feat: coder session title

### Verdict: PASS (with 1 fix applied)

### Completeness

- All 4 capabilities from the proposal (1 new, 3 modified) have corresponding spec files ✓
- All spec requirements are mapped to tasks (T-001 through T-007) ✓
- Edge cases covered: nullable title, executionId fallback, stage fallback, taskDescription fallback ✓

### Consistency

- Naming consistent (`title`, not `label`) across all artifacts ✓
- Design decisions (D1–D6) align with spec requirements ✓
- Task spec references point to correct files and requirement anchors ✓

### Fix Applied

**Caller table incomplete in spec**: `specs/coder-session-title/spec.md` listed only 7 callers while design (D6) and tasks (T-003) correctly identified 9 callers (adding `conflict-resolution.ts` and `server/index.ts`). Updated the spec table and added 2 matching scenarios to maintain consistency.

### Dependency Graph Validation

```
T-001 (DB + repo) ──┬──→ T-002 (ACP options + SSE) ──→ T-003 (9 callers)
                    ├──→ T-004 (API) ──→ T-005 (frontend types) ──→ T-006 (frontend display)
                    └──→ T-004 + T-001 ──→ T-007 (tests)
```

- DAG, no cycles ✓
- All `dependsOn` reference lower-priority tasks ✓
- All task IDs exist in the list ✓
- Every non-first task has at least one dependency ✓

### Feasibility

- Each task is completable in a single agent iteration ✓
- T-001 correctly bundles migration + repo changes (neither delivers value alone) ✓
- Task granularity appropriate — no task is too small or too large ✓
