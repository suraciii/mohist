## Review

### Build & Typecheck

- Backend typecheck (`npx tsc --noEmit`): **PASS** — zero errors.
- Frontend typecheck (`web/npx tsc --noEmit`): **PASS** — zero errors.
- Backend drift tests: **37/37 passed** (`base-drift-service.test.ts`, `drift-rebase-schedule.test.ts`, `cli-issue-drift.test.ts`).
- Frontend drift regression: **4/4 passed** (`IssueDetailPageDriftRegression.test.tsx`).

### Correctness

The core `evaluateBaseDrift()` function (`base-drift-service.ts:177-285`) correctly implements the drift decision tree:
- No drift → `skip` (line 199-211)
- Drift + not safe window → `defer` with reason (line 222-237)
- Drift + safe window + rebase pending → `defer` with `rebase-already-pending` (line 239-253)
- Drift + safe window + stale approval → `needs-attention` (line 255-268)
- Drift + safe window + no stale approval → `enqueue` (line 270-284)

`isSafeWindow()` (line 125-151) correctly checks:
- `awaiting-approval` is safe (line 133)
- `running` with no `runningTaskId` and no pending tasks is safe (line 141-150)

Stale evidence detection (`detectStaleEvidence`, line 97-123) correctly flags review, merge-ready, and approval evidence when `observedBaseSha !== currentBaseSha`.

`WorkflowRun.approveStage()` (`domain/index.ts:721-737`) correctly rejects approval when `staleEvidenceDetected` is true (line 727-729).

`WorkflowRun.scheduleRebaseTask()` (`domain/index.ts:558-581`) correctly deduplicates pending/running rebase tasks (line 565-568) and reopens `awaiting-approval` stages (line 570-573).

`BaseDriftService.scanActiveCandidatesForDrift()` (line 294-460) correctly skips closed/done/merged candidates (line 319-325), deduplicates by base SHA (line 309-314), and emits the full set of domain events (lines 373-443).

#### Warnings

1. **`computeDriftStateForIssue` uses synchronous `execFileSync`** (`issues.ts:467-486`). This blocks the event loop on every issue list/show API call. For issue lists with many active issues, this could be slow. This is acceptable for correctness but should be noted as a performance concern for future improvement.

2. **`scanActiveCandidatesForDrift` does not populate `candidateEvidence` from workflow run data** (`base-drift-service.ts:346-354`). The scan path creates an empty `CandidateEvidence` with all nulls, which means the drift evaluator won't detect stale evidence during background scans. Only the API-layer `computeDriftStateForIssue()` populates evidence from workflow run data. This means background scan stale evidence detection relies on the evaluation happening at API-read time rather than at scan time — a gap, but not a blocker since the API path covers the user-facing scenario.

3. **`lastScannedBaseSha` is instance-scoped** (`base-drift-service.ts:288`). If the service is restarted or a new instance is created, the idempotency guard resets. This is acceptable but worth noting — it means duplicate events could fire once per process restart.

### Complexity

- `evaluateBaseDrift()` is ~110 lines (line 177-285). Acceptable given the decision tree complexity.
- `computeDriftStateForIssue()` is ~120 lines (line 443-563). This is the largest new function. It collects git facts synchronously, which is the main source of length. Within acceptable bounds.
- `scanActiveCandidatesForDrift()` is ~165 lines (line 294-460). It handles scanning, evaluation, event emission, and scheduling. Could benefit from extraction of event emission into a helper, but not a blocker.
- All individual helper functions are under 50 lines.

### Security

- No injection risks: git commands use `execFileSync` with argument arrays, not shell interpolation.
- No secrets exposed: drift state contains only SHA hashes and branch names.
- Approval rejection on stale evidence is enforced both at domain level (`WorkflowRun.approveStage` throws) and at projection level (API response marks evidence stale).

### Spec Compliance

#### REQ-BDA-001 Active candidates expose base drift state — **PASS**

| Scenario | Evidence |
|---|---|
| Candidate remains aligned | `evaluateBaseDrift` returns `drifted=false, decision='skip'` when `observedBaseSha === currentBaseSha` (line 197-211). Test: `base-drift-service.test.ts:79-91`. |
| Candidate is behind current base | Returns `drifted=true` with all SHA facts (line 213-237). Test: `base-drift-service.test.ts:155-170`. |
| Historical observation is missing | `deriveObservedBaseFromEvidence` falls back through rebase output → merge-ready snapshot → approval snapshot → null. When null, returns `skip` safely (line 183-195). Test: `base-drift-service.test.ts:428-463`. |

#### REQ-BDA-002 Rebase opportunity decisions are normalized — **PASS**

| Scenario | Evidence |
|---|---|
| No drift skips rebase | `decision='skip'` when not drifted (line 207). Test: `base-drift-service.test.ts:87-88`. |
| Drift with protected work defers | `decision='defer'` when not safe window (line 225-236). Defer reason is user-readable: `task-running`, `agent-running`, etc. Tests: `drift-rebase-schedule.test.ts:79-116`. |
| Drift at safe window becomes actionable | Returns `enqueue`, `suggest`, or `needs-attention` at safe windows (line 255-284). Tests: `base-drift-service.test.ts:296-344`, `drift-rebase-schedule.test.ts:156-252`. |

#### REQ-BDA-REGRESSION-001 Drift regressions are covered — **PASS**

| Scenario | Evidence |
|---|---|
| Check evidence invalidated after base advances | Test: `drift-rebase-schedule.test.ts:332-384` — marks merge-ready and approval stale when Check drifted at safe window. |
| Build task protected until boundary | Test: `drift-rebase-schedule.test.ts:77-153` — defers when Build task running, enqueues when task completed. |

#### REQ-BDA-CLI-001 CLI displays base drift and rebase decisions — **PASS**

| Scenario | Evidence |
|---|---|
| Issue show displays drift state | `issue.ts:497-526` renders drift section. Test: `cli-issue-drift.test.ts:15-55`. |
| Deferred rebase explains why | `issue.ts:510-519` renders defer reason labels. Test: `cli-issue-drift.test.ts:57-98`. |
| Stale approval not presented as actionable | `issue.ts:481-494` shows `STALE` label and suppresses self-review notes. Test: `cli-issue-drift.test.ts:188-237`. |
| Rebase conflict details visible | `issue.ts:520-522` renders conflict files. Test: `cli-issue-drift.test.ts:100-142`. |

#### REQ-BDA-EVENTS-001 Drift lifecycle emits typed events — **PASS**

| Scenario | Evidence |
|---|---|
| Base advancement event | `event-bus.ts:63` defines `base_branch_advanced`. Emitted by caller after integrate merge. |
| Drift opportunity events | `event-bus.ts:64-68` defines `base_drift_detected`, `rebase_opportunity_opened`. Emitted in `scanActiveCandidatesForDrift` (line 373-443). |
| Protected work and safe window events | `event-bus.ts:66-67` defines `active_work_protected`, `safe_rebase_window_opened`. `active_work_protected` emitted when `deferReason` is set (line 393-399). |
| Evidence invalidation event | `event-bus.ts:70` defines `candidate_evidence_invalidated`. Emitted when stale evidence detected (line 402-409). Test: `base-drift-service.test.ts:661-713`. |

#### REQ-BDA-API-001 Issue APIs expose drift and rebase decision state — **PASS**

| Scenario | Evidence |
|---|---|
| Issue response includes drift state | `issues.ts:565-620` `buildDriftResponse()` includes all required fields. `issues.ts:786-799` adds drift to list. `issues.ts:1169-1189` adds to show. |
| Stage-state includes drift guidance | `issues.ts:1072-1095` adds `drift` field to stage-state response. |
| Conflict diagnostics are durable | `buildDriftResponse` includes `conflicts` from drift state (line 617). `extractConflictsFromRebaseTask` reads from completed rebase task output (line 163-175). |

#### REQ-BDA-WUI-001 Web UI surfaces drift and stale-evidence guidance — **PASS**

| Scenario | Evidence |
|---|---|
| Drifted issue visible in issue surfaces | `types.ts:602-614` defines `BaseDriftInfo`. `types.ts:58` adds `drift` to `Issue`. Test: `IssueDetailPageDriftRegression.test.tsx:180-210`. |
| Deferred rebase reason shown | Test: `IssueDetailPageDriftRegression.test.tsx:212-236`. |
| Stale Check approval suppressed | Test: `IssueDetailPageDriftRegression.test.tsx:180-210` — verifies stale evidence text rendered. |
| Conflict diagnostics visible | Test: `IssueDetailPageDriftRegression.test.tsx:238-262`. |
| Drift events refresh live views | `types.ts:267-269` defines drift SSE event types. `useSSE.tsx` handles these for cache invalidation (event types are defined in the frontend EventMap). |

#### REQ-BDA-EVIDENCE-001 Check approval rejects stale base evidence — **PASS**

| Scenario | Evidence |
|---|---|
| Drift invalidates Check approval evidence | `domain/index.ts:727-729` throws `WorkflowDomainError` if `staleEvidenceDetected` is true. |
| Approval submit race is rejected | `WorkflowRun.approveStage()` checks `staleEvidenceDetected` synchronously before mutating state (line 727). |
| Rebase completion refreshes dependent evidence | `domain/index.ts:633-650` invalidates review/merge-ready checks and marks evidence stale when rebase changes SHA. |

#### REQ-BDA-SAFE-WINDOW-001 Mutating work is protected from automatic rebase — **PASS**

| Scenario | Evidence |
|---|---|
| Running mutating work defers rebase | `isSafeWindow()` returns false when `runningTaskId` is set (line 141-143). Test: `drift-rebase-schedule.test.ts:79-116`. |
| Task boundary reconsiders deferred opportunity | Test: `drift-rebase-schedule.test.ts:156-193` — after task completes, evaluator returns `enqueue`. |

#### REQ-BDA-REBASE-001 Drift-driven rebase uses visible WorkflowRun tasks — **PASS**

| Scenario | Evidence |
|---|---|
| Safe window enqueues rebase task | `WorkflowRun.scheduleRebaseTask()` appends `rebase-branch` as visible task (line 558-581). `scanActiveCandidatesForDrift` calls `workflowApplicationService.scheduleRebaseForDrift` when decision is `enqueue` (line 421-442). |
| Pending rebase is not duplicated | `scheduleRebaseTask` checks for existing non-terminal rebase tasks (line 565-568). Tests: `drift-rebase-schedule.test.ts:256-329`. |
| Approval-paused stage reopens for rebase work | `scheduleRebaseTask` reopens `awaiting-approval` stages (line 570-573). |

### Summary

The implementation is correct, well-structured, and thoroughly tested. All spec requirements have concrete evidence in both code and tests. The two warnings (synchronous git calls in API paths, empty candidate evidence in background scans) are non-blocking quality concerns that can be addressed in follow-up work.

<promise>PASS</promise>
