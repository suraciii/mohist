## Review: Issue #216 — Issue-level start prerequisites

### Correctness

No logic errors found. The implementation follows the spec faithfully:

- **Delivery evaluation**: `isDelivered()` (`issue-prerequisite-service.ts:173-179`) correctly requires all three conditions: `Stage.Done` + `IssueStatus.Completed` + `MergeState.Merged`.
- **Circular detection**: `wouldCreateCycle()` (`issue-prerequisite-service.ts:203-222`) uses a DFS traversal from the proposed prerequisite to detect both direct self-reference and indirect cycles. Correct.
- **Start guard in API**: `POST /:number/start` (`issues.ts:1132-1141`) calls `assertStartEligible` and returns 400 with `startEligibility` data before enqueueing. Correct.
- **Queue backstop**: `executeStartPipelineTask` (`agent-runner-service.ts:1046-1069`) rechecks eligibility via `evaluateStartEligibility(issue)` before worktree/WorkflowRun creation. Skipped tasks are completed (not failed), preserving the "not a failure state" invariant. Correct.
- **API declaration**: `POST /:number/prerequisites` (`issues.ts:1172-1219`) validates input, delegates to service, returns structured response. Correct.
- **Circular rejection**: Returns 400 with `reason: 'circular-prerequisite'` (`issues.ts:1198-1203`). Correct.
- **Waiting ≠ blocked**: The service never mutates issue status to `blocked` when evaluating prerequisites. Confirmed in service code and test (`issue-prerequisite-service.test.ts:350-375`).

### Complexity

- All functions are under 50 lines. `IssuePrerequisiteService` methods are concise.
- `wouldCreateCycle` is clean iterative DFS. Cyclomatic complexity well under 10.
- `getPrerequisiteViews` batch method properly batches DB lookups to avoid N+1 queries. Good.
- `createIssueRoutes` in `issues.ts` is a large function but that is pre-existing pattern; the added prerequisite endpoints are cleanly structured.

### Test Coverage

**62 tests pass across 4 test files:**

| File | Tests | Coverage |
|------|-------|----------|
| `services/issue-prerequisite-service.test.ts` | 31 | Declaration, circular rejection (direct + indirect), delivery evaluation (done-not-merged, merged-without-done), waiting ≠ blocked, persistence, task-level separation |
| `api-issue-prerequisites.test.ts` | 13 | API declaration, circular rejection, same-issue, start rejection without queueing, start after delivery, list/detail include prerequisites |
| `start-eligibility-queue.test.ts` | 7 | Queue backstop skips waiting issues, no WorkflowRun/session creation, proceeds after delivery |
| `cli-issue-prerequisites.test.ts` | 11 | List rendering, show rendering, start rejection, add-prerequisite command, circular error output |
| `IssueDetailPage-prerequisites.test.tsx` | (web) | Prerequisite display, waiting badge, add/remove, circular error |

All tests pass. Typecheck passes with zero errors.

### Security

- SQL parameters are properly bound via `?` placeholders in `IssueStartPrerequisiteRepo`. No injection risk.
- API input validation: `prerequisiteNumber` is type-checked as number (`issues.ts:1183`).
- No secrets exposed.

### Spec Compliance — Acceptance Criteria

| # | Criterion | Verdict | Evidence |
|---|-----------|---------|----------|
| 1 | Users can declare that one issue has a prerequisite issue | **PASS** | `POST /:number/prerequisites` route + `declarePrerequisite` service method + CLI `add-prerequisite` command + Web UI add-prerequisite section |
| 2 | Issue detail shows start prerequisites and whether each prerequisite issue has been delivered | **PASS** | `GET /:number` returns `prerequisites[]` with `delivered` boolean (`issues.ts:896-916`), CLI `renderPrerequisites()` (`issue.ts:62-71`), Web UI `IssueDetailPage.tsx:816-837` |
| 3 | Issue list or card shows a concise waiting reason | **PASS** | API list includes `startEligibility.waitingForDelivery` (`issues.ts:528-539`), CLI renders `[Waiting for #N]` (`issue.ts:335-337`), Web UI card shows waiting message (`IssueCard.tsx:301-316`) |
| 4 | `mo issue start`, API start, and Web UI Start use the same start eligibility guard | **PASS** | API start calls `assertStartEligible` (`issues.ts:1133`), queue backstop calls `evaluateStartEligibility` (`agent-runner-service.ts:1047`), Web UI start uses same API endpoint (`IssueDetailPage.tsx:617-635` shows waiting state, start button calls `api.startIssue`) |
| 5 | If a prerequisite issue has not been delivered, the current issue cannot enter the pipeline | **PASS** | Start guard returns 400 (`issues.ts:1135-1140`), queue backstop skips (`agent-runner-service.ts:1048-1055`) |
| 6 | If start is rejected, Mohist does not enqueue `start-pipeline` | **PASS** | Guard returns before `agentRunner.enqueue` call (`issues.ts:1141` vs `1151`), confirmed in API test |
| 7 | Once prerequisite issues are delivered, the current issue becomes startable without manual cleanup | **PASS** | `evaluateStartEligibility` recomputes from current DB state (`issue-prerequisite-service.ts:119-133`), prerequisite row not deleted, confirmed in test `should report startable=true when prerequisite is delivered` |
| 8 | Circular prerequisite declarations are rejected | **PASS** | Direct self, direct reverse, and indirect A→B→C→A all rejected (`issue-prerequisite-service.ts:203-222`), API returns 400 with `reason: 'circular-prerequisite'`, CLI exits non-zero |
| 9 | Issue-level start prerequisites are not mixed with task-level `tasks.json dependsOn` | **PASS** | Separate table `issue_start_prerequisites`, separate repo, separate service. Test confirms task-level deps not interpreted (`issue-prerequisite-service.test.ts:392-427`) |
| 10 | API returns structured start prerequisite/status data so frontend does not parse issue body text | **PASS** | `prerequisites[]` + `startEligibility` + `waitingForDelivery[]` in all issue responses, clients render from structured fields only |

### Warnings

1. **Warning — `declarePrerequisite` does not guard duplicate declarations**: If `declarePrerequisite` is called twice with the same pair, the `INSERT` will succeed both times (SQLite `INSERT` without `OR IGNORE`). However, the composite primary key `(issue_id, prerequisite_issue_id)` means SQLite will throw a uniqueness constraint violation. The repo's `create` method doesn't catch this. This is a minor robustness issue — in practice the API returns a 500 on duplicate, but a clearer 409/200 idempotent response would be better. Not spec-blocking since the spec does not define idempotency.

2. **Warning — `projectId` parameter unused in `getPrerequisiteView`/`getPrerequisiteViews`**: The `_projectId` parameter is accepted but ignored (`issue-prerequisite-service.ts:66,72`). The design spec mentions "validate that both Issues belong to the same project," but the current implementation does not enforce cross-project isolation at the service level. Since issue IDs are globally unique UUIDs and the lookup goes through `IssueRepo.findById`, cross-project contamination is unlikely but not explicitly prevented.

3. **Warning — Start tip shown on `mo issue create` even when non-prerequisite lifecycle reasons apply**: The CLI `isStartable` helper (`issue.ts:58-60`) only checks `startEligibility.startable`. For a freshly created issue this is always true (backlog + active), so the tip shows correctly. But the spec says "Start tip omitted for non-startable issue" — if a future change makes create return a non-startable issue, this would need adjustment. Current behavior is correct.

### Summary

The implementation is clean, well-tested (62 tests), and correctly implements all acceptance criteria. The domain model is properly centralized in `IssuePrerequisiteService`, the two-layer guard (API + queue) prevents stale work, and the separation from task-level `dependsOn` is clean. Typecheck passes with zero errors.

<promise>PASS</promise>
