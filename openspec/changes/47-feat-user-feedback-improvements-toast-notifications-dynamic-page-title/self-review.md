# Self-Review: Issue #47 — Toast Notifications + Dynamic Page Title

**Date:** 2026-05-01
**Reviewer:** Agent (self-review)
**Verdict:** PASS (with fixes applied)

## Artifacts Reviewed

| Artifact | Status |
|---|---|
| `proposal.md` | PASS |
| `specs/toast-notifications/spec.md` | PASS (fixed) |
| `specs/dynamic-page-title/spec.md` | PASS |
| `specs/web-ui/spec.md` | PASS |
| `design.md` | PASS |
| `tasks.json` | PASS (fixed) |

## Review Criteria

### Completeness

- **All proposal capabilities have specs**: `toast-notifications` (new), `dynamic-page-title` (new), `web-ui` (modified) — all covered.
- **All spec requirements have tasks**: 9 requirements across 3 spec files are covered by 6 tasks.
- **Edge cases**: Current-issue suppression for SSE toasts is specified; page title restore-on-unmount is specified; mutation error fallback message is specified.

### Consistency

- Proposal capabilities match spec directory names exactly: `toast-notifications`, `dynamic-page-title`, `web-ui`.
- Design decisions (D1–D5) align with spec requirements.
- `web-ui` delta spec MODIFIED requirement header (`Web UI 实时响应 agent 暂停状态`) matches the existing spec in `openspec/specs/web-ui/spec.md` exactly.
- Task spec references point to valid files and requirement anchors.

### Feasibility

- T-001 and T-002 are independent and can run in parallel — correct.
- T-003 and T-004 both depend on T-001 (sonner must be installed) — correct.
- T-005 depends on T-002 (useDocumentTitle hook must exist) — correct.
- T-006 depends on T-003, T-004, T-005 (all code must be in place before verification) — correct.
- No circular dependencies. All `dependsOn` reference lower-priority tasks. DAG validated.

### Dependency Completeness

- T-001: `dependsOn: []` (root task, P1) — valid.
- T-002: `dependsOn: []` (root task, P1) — valid.
- T-003: `dependsOn: ["T-001"]` (P2 → P1) — valid.
- T-004: `dependsOn: ["T-001"]` (P3 → P1) — valid.
- T-005: `dependsOn: ["T-002"]` (P3 → P1) — valid.
- T-006: `dependsOn: ["T-003", "T-004", "T-005"]` (P4 → P2/P3/P3) — valid.

## Issues Found and Fixed

### Issue 1: Missing mutations in spec (Fixed)

**File:** `specs/toast-notifications/spec.md`

Three user-initiated mutations in `useQueries.ts` were missing from the spec's mutation table:
- `useCreateExploreSession`
- `useUpdateExploreSessionTitle`
- `useTestProvider`

**Fix:** Added all three to the mutation table with appropriate messages, plus a scenario for `useTestProvider`.

### Issue 2: T-003 incomplete spec reference (Fixed)

**File:** `tasks.json`

T-003 only referenced `#mutation-success-toasts` but also covers `#mutation-error-toasts` per its acceptance criteria. Mutation count was stale (15 → should be 18).

**Fix:** Updated description to reference 18 hooks. Added `#mutation-error-toasts` coverage note to the `notes` field.

### Issue 3: T-005 incomplete spec reference (Fixed)

**File:** `tasks.json`

T-005's `spec` field only pointed to `#dynamic-page-title-reflects-current-route` but the task also covers `#page-title-indicates-live-agent-activity` (agent running indicator).

**Fix:** Added explicit reference to `#page-title-indicates-live-agent-activity` in the description.

## Remaining Notes

- The `Toast trigger API` spec requirement says "WebUI SHALL export a `useToast` hook (or equivalent)". Design D1 uses sonner's `toast` directly without a wrapper. The "(or equivalent)" phrasing covers this — sonner's module-level `toast` function IS the API. No export wrapper needed.
- SSE event payloads for `merge_completed` and `merge_failed` must include `issueNumber` — the implementing agent should verify this in the actual event types during T-004 execution.
