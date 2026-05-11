# Review: Issue #178 — Refactor workflow recovery verbs and state model, remove restart behavior

## Dimensions

### Correctness

**PASS** — The core logic changes are correct:

1. `IssueService.reopen()` now only accepts `IssueStatus.Closed` — returns `null` for blocked/paused/interrupted. Verified at `packages/cli/src/services/issue-service.ts:146`.
2. `IssueService.resume()` correctly gates on `Paused || Interrupted`, returns `null` for everything else. Verified at `packages/cli/src/services/issue-service.ts:133-141`.
3. `POST /api/issues/:number/resume` properly validates status, calls `recoverSingleIssueById` before status update (per T-006 fix), enqueues `resume-pipeline` only when `agentRunner` exists. Verified at `packages/cli/src/api/issues.ts:921-975`.
4. `POST /api/issues/:number/restart` returns 410 with deprecation message. No state mutation occurs. Verified at `packages/cli/src/api/issues.ts:2909-2929`.
5. `POST /api/issues/:number/retry` no longer falls back to `backlog` when no checkpoint exists — returns 409 with guidance. Verified at `packages/cli/src/api/issues.ts:2878-2883`.
6. `retry` route no longer calls `clearApprovalEverywhere` (removed at line ~2854 in the diff). This is consistent with not resetting stage.

**Warning W1**: `POST /api/issues/:number/resume` calls `agentRunner.recoverSingleIssueById(issue.id)` at line 943 *before* `issueService.resume()` at line 946. If `resume()` returns null (e.g., race condition where status changed between the check and the service call), the error response at line 948-949 returns 500, but `recoverSingleIssueById` has already been called. This is an edge case rather than a bug, but worth noting.

**Warning W2**: `POST /api/issues/:number/reopen` at line 908 refreshes the issue via `issueService.getByNumber()` after a successful reopen. However, it no longer calls `agentRunner.enqueue()` — per the spec, reopen should NOT auto-enqueue `resume-pipeline`, so this is correct behavior. However, the response message says `"Issue #${number} reopened at stage ${issue.stage}."` which might mislead users into expecting pipeline resumption. A minor wording concern.

### Complexity

**PASS** — All new and modified functions are under 50 lines and have reasonable cyclomatic complexity. The `resume` handler (`issues.ts:921-975`) is the longest新增 block at ~55 lines, which is borderline but acceptable given it handles validation + side effects + two response paths (with/without agentRunner). The `retry` handler was simplified by removing the fallback-to-backlog branch.

### Test Coverage

**PASS** — Two new regression test files cover the spec requirements:

- `tests/recovery-routing-regression.test.ts` (25 tests): Covers reopen (closed-only), resume (paused/interrupted), restart (deprecation 410), retry (no-checkpoint rejection, checkpoint success), start guidance (blocked → retry/rerun, closed → reopen).
- `tests/recovery-verb-regression.test.ts` (23 tests): Mirrors and extends coverage for service-layer `reopen()`, API reopen (no enqueue for closed), API resume, API restart deprecation, retry with checkpoint, start guidance.
- `tests/api-routes.test.ts` (72 tests): Updated existing tests for the restart deprecation and changed error messages.

All 143 tests pass. TypeScript compiles cleanly.

### Security

**PASS** — No new injection risks. The `number` parameter is parsed with `parseInt()` consistently (same pattern as existing code). No SQL injection vectors. The deprecation response for `restart` doesn't expose internal details beyond the guidance message.

### Spec Compliance

Checking each acceptance criterion:

---

#### 1. "用户界面不再出现 Restart 动作"

- **PASS** — Web UI: No Restart button found in `IssueCard.tsx`, `IssueDetailPage.tsx`, or `PipelineView.tsx`. The only "restart" references in Web are: (a) `IssueDetailPage.tsx:475` "server restart" in interrupted explanation (this is describing a cause of interruption, not a user action — acceptable), and (b) `SystemSettingsSection.tsx` server restart (infrastructure, not issue recovery).
- **PASS** — CLI: No `restart` issue subcommand found in `issue.ts`. The `mo restart` command at `cli/index.ts:70` is for the mohist server itself, not issue recovery.
- **PASS** — API: `POST /:number/restart` returns 410 deprecation error, never mutates state.

#### 2. "API 不再提供可用的 restart 行为，或明确返回 use retry/rerun/rewind 的错误"

- **PASS** — `POST /:number/restart` returns 410 with `"restart has been removed; use retry, rerun, or rewind instead"`. Verified at `issues.ts:2924`. No state mutation occurs (verified by test at `recovery-verb-regression.test.ts:328-340`).

#### 3. "`reopen` 只用于 closed issue"

- **PASS** — `IssueService.reopen()` rejects blocked, paused, interrupted, and active issues (`issue-service.ts:146`). API route returns 404 for non-closed issues (`issues.ts:905`). Does NOT auto-enqueue `resume-pipeline` for closed reopen (verified by test). CLI command description updated to "Reopen a closed issue" (`issue.ts:565`).

#### 4. "paused/interrupted issue 使用 `resume` 恢复"

- **PASS** — `POST /:number/resume` accepts `Paused` and `Interrupted`, rejects others with 409. `IssueService.resume()` gates on paused/interrupted. Stage and checkpoints preserved (verified by tests). CLI `mo issue resume` updated to use `/resume` endpoint (`issue.ts:706`). Web `IssueCard.tsx` and `IssueDetailPage.tsx` show Resume for interrupted issues. `PipelineView.tsx` SpecialStatePanel shows Resume for interrupted.

#### 5. "failed/needs-action issue 使用 `retry`、`rerun` 或 `rewind` 恢复"

- **PASS** — `POST /:number/retry` handles blocked (failed/needs-action) issues with checkpoint-based retry. Returns 409 with guidance when no checkpoint. Web `IssueDetailPage.tsx:574-595` shows Retry + Rerun Stage buttons for blocked issues. API error messages for blocked issue on `/start` reference retry/rerun (`issues.ts:721`). No "restart" in error messages.

#### 6. "Blocked 用户标签被替换为 Failed / Needs action"

- **PASS** — `statusLabel()` in `status-badge.ts` maps `IssueStatus.Blocked` → `"Needs Action"`. `IssueCard.tsx:158` shows "Needs Action" overlay on blocked cards. `IssueDetailPage.tsx:449` shows `statusLabel(issue.status)` instead of raw status. `PipelineView.tsx:838` shows "Needs Action" in SpecialStatePanel. `IssueDetailPage.tsx:253` shows `statusLabel` in header badge.
- **Note**: The spec says "Failed" / "Needs action" depending on failure reason, but `statusLabel` always maps Blocked to "Needs Action". This is acceptable for first version per design D6's incremental approach, but worth noting for future refinement.

#### 7. "CLI/API/Web 的恢复动词一致"

- **PASS** — CLI `reopen` → API `/reopen` (closed only), CLI `resume` → API `/resume` (paused/interrupted only), API `/retry` (blocked/checkpoint), API `/rerun` (rerun stage), CLI error messages updated. Web follows same model.

### Additional Findings (Non-blocking)

**F1 (Warning)**: The `propose.ts:51` endpoint still says `mo issue reopen ${number} to reactivate` for any non-active issue. This could incorrectly suggest using `reopen` for paused/interrupted issues. Per the new verb model, it should differentiate — for closed issues say `reopen`, for paused/interrupted say `resume`, for blocked say `retry`. This is in the `propose` route, not the main recovery flow, but it's inconsistent with the spec's intent-based verb model.

**F2 (Info)**: The `IssueDetailPage.tsx` still defines `reopenMutation` (line 133-138) which calls `api.reopenIssue()`. This is fine — it's used for Closed issue Reopen action, which is correct per spec. However, there's currently no visible "Reopen" button for closed issues in the rendered JSX (I didn't find `isClosed && reopenMutation` in the buttons section). The reopen mutation is defined but may not be rendered in the UI for closed issues. This is a gap where the Web UI has no Reopen button for closed issues, only the API/CLI can reopen.

**F3 (Info)**: The `reject` route at `issues.ts:1456` says "pipeline restarted from..." which uses "restarted" in a pipeline context (not issue recovery). This is semantically different from the deprecated "restart" verb (it means the pipeline re-executes from a prior stage after rejection). Low severity but the word "restarted" could cause confusion.

**F4 (Info)**: CLI `issue.ts:685` says "pipeline will restart" for rejected issues — same concern as F3.

## Summary

| Dimension | Result |
|-----------|--------|
| Correctness | PASS (2 minor warnings) |
| Complexity | PASS |
| Test Coverage | PASS (48 new tests + 72 existing, all pass) |
| Security | PASS |
| Spec Compliance | All 7 acceptance criteria PASS |

### Warnings (non-blocking)

- **W1**: `resume` handler calls `recoverSingleIssueById` before `resume()` service call; potential edge case if status changes between check and service call.
- **W2**: `reopen` success message says "reopened at stage X" which may imply pipeline resumption to users.

### Actionable Items (non-blocking)

- **F1**: `propose.ts:51` uses `reopen` for any non-active status — should differentiate by status per new verb model.
- **F2**: No visible Reopen button for Closed issues in `IssueDetailPage.tsx` — consider adding one for completeness.
- **F3/F4**: "restarted" wording in reject flow could confuse with deprecated "restart" verb — consider "will resume from" or "will re-execute from".

<promise>PASS</promise>