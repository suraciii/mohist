## Self-Review: Issue #128 — feat: coder session title

**Reviewer:** opencode agent
**Date:** 2026-05-02

### Verdict: PASS — all artifacts are consistent and complete

---

### Completeness

| Spec requirement | Task | Status |
|---|---|---|
| Coder session title stored on creation | T-001 | Covered |
| Each caller supplies descriptive title | T-003 | Covered |
| SSE coder_session_started carries title | T-002 | Covered |
| API responses include session title | T-004 | Covered |
| Frontend priority fallback chain | T-005 | Covered |
| coder-session-tracking: title on insert | T-002 | Covered |
| agent-activity-page: session cards show title | T-006 | Covered |
| session-list-ui: SessionHeader fallback chain | T-005 | Covered |
| session-list-ui: useCoderSessions with title from SSE | T-006 | Covered |

All proposal capabilities have corresponding specs. All specs have tasks.

### Consistency

- Specs align with proposal Capabilities section (4 capabilities: 1 new, 3 modified)
- Tasks reference correct spec files and requirement anchors
- Design decisions match specs (nullable column, options interfaces, caller titles, fallback chain)
- Naming consistent: `title` used throughout (not `label`)
- **Fix applied**: Proposal updated from 7 to 8 callers to include `conflict-resolution.ts` (discovered during design exploration via D5)

### Feasibility

- T-001 (migration + repo) has no dependencies — correct foundation
- T-002 (ACP options) depends on T-001 (needs repo interface) — correct
- T-003 (callers) depends on T-002 (needs options interface) — correct
- T-004 (API) depends on T-001 only (needs repo query changes, not ACP layer) — correct, enables parallelism
- T-005 (frontend types + label) depends on T-004 (needs API returning title) — correct
- T-006 (hooks + cards) depends on T-005 (needs types defined) — correct

### Dependency Graph Validation

- Valid DAG — no cycles
- Every `dependsOn` references a task with strictly lower priority number
- All referenced task IDs exist
- Linear chain with one parallel branch: T-001 → {T-002 → T-003, T-004 → T-005 → T-006}

### Issues Found and Fixed

1. **Proposal caller count mismatch**: Originally listed 7 callers. Design exploration (D5) discovered `conflict-resolution.ts` as an 8th caller. Updated proposal's "What Changes" and "Impact" sections to reflect 8 callers.

### Notes

- SkillService is included in T-003 per the spec, even though it doesn't currently create `coder_session` rows (no `coderSessionRepo` passed). This is correct for forward compatibility and matches the spec's caller table.
- `session-timeline-ui/spec.md` was not modified because the timeline component uses round labels, not session titles — no spec-level change needed there.
