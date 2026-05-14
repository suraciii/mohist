## Findings

1. High: `IssuePrerequisiteService` cannot read back persisted prerequisites because it looks up by issue id using the issue-number API.
File: `packages/cli/src/services/issue-prerequisite-service.ts:66-69,143-149`
`getPrerequisiteView()` calls `getPrerequisiteSummaries(issue.id)`, but `getPrerequisiteSummaries()` uses `issueRepo.findById()` only for prerequisite issues after loading rows from `prerequisiteRepo.findByIssue(issueId)`. The tests show the prerequisite row is not being surfaced at all: `tests/services/issue-prerequisite-service.test.ts:241-244`, `:349-350` fail with empty arrays, and `evaluateStartEligibility()` returns `startable=true` even after declaration (`:163-170`, `:303-307`). This breaks the core domain behavior, API projections, and start guard.
Suggested fix: keep the repo lookup keyed by issue id end-to-end and add a regression test around `declarePrerequisite()` -> `getPrerequisiteView()` -> `assertStartEligible()` on the same service instance.

2. High: `POST /api/issues` does not return `startEligibility` or `prerequisites`, so CLI create cannot satisfy the spec that start hints depend on server-provided eligibility.
File: `packages/cli/src/api/issues.ts:604-610`, `packages/cli/src/cli/commands/issue.ts:240-246`
The create route returns the raw `Issue` only. The CLI `create` command then falls back to `issue.stage === Stage.Backlog` in `isStartable()` when `startEligibility` is absent (`packages/cli/src/cli/commands/issue.ts:58-63`), which is exactly the client-side decision the spec forbids for this feature. A newly created issue that is waiting for delivery would still get the `mo issue start` tip.
Suggested fix: project the created issue through `IssuePrerequisiteService.getPrerequisiteView()` in `POST /api/issues`, and remove the stage-based fallback from CLI start-tip logic for create output.

3. High: the CLI prerequisite declaration command required by the spec is not implemented.
File: `packages/cli/src/cli/commands/issue.ts`
There is no command registration for `add-prerequisite`, `prerequisite add`, or equivalent. A grep over the command file only finds rendering types and display code, not a declaration command. The test suite expects `issue add-prerequisite 201 200` (`tests/cli-issue-prerequisites.test.ts:358-417`), but the command does not exist. This leaves the CLI acceptance criteria for structured declaration and circular-error surfacing unmet.
Suggested fix: add an explicit CLI command that POSTs to `/api/issues/:number/prerequisites`, prints the updated prerequisite state, and maps `reason === 'circular-prerequisite'` to a clear non-zero error.

4. Medium: the Web UI only renders prerequisite details for backlog issues, but the spec requires Issue Detail to show prerequisites whenever the API includes them.
File: `packages/cli/web/src/components/IssueDetailPage.tsx:816-840`
The prerequisite display is gated by `isBacklog && issue.prerequisites && issue.prerequisites.length > 0`. For plan/build/check/done issues, Issue Detail hides prerequisites entirely even though the API detail shape includes them. This violates `specs/web-ui/spec.md` requirement “Issue Detail SHALL display issue-level start prerequisites”.
Suggested fix: render the prerequisite summary section independently of `isBacklog`; keep only the declaration controls backlog-only if desired.

5. Medium: regression coverage is not passing, so the feature is not demonstrably complete.
Files: `packages/cli/tests/services/issue-prerequisite-service.test.ts`, `packages/cli/web/src/components/IssueDetailPage-prerequisites.test.tsx`
Focused test run failed with 42 failures and 23 passes:
`npm test -- --run tests/services/issue-prerequisite-service.test.ts tests/api-issue-prerequisites.test.ts tests/cli-issue-prerequisites.test.ts tests/start-eligibility-queue.test.ts web/src/components/IssueDetailPage-prerequisites.test.tsx`
The failures include the broken service behavior above and multiple web tests that do not match the rendered UI, so the claimed regression coverage is currently not green.
Suggested fix: first fix the prerequisite service and missing CLI command, then update or add tests so the targeted backend/CLI/web prerequisite suites pass consistently.

## Open Questions

- `IssueDetailPage` shows two separate start buttons in the current render tree during tests. If both are intentional, the test selectors should be scoped; if not, there may be a duplicated action surface worth simplifying.

## Spec Compliance

| Acceptance Criterion | Status | Evidence |
| --- | --- | --- |
| Users can declare that one issue has a prerequisite issue | FAIL | API endpoint exists in `packages/cli/src/api/issues.ts:1196-1239`, but required CLI declaration path is missing from `packages/cli/src/cli/commands/issue.ts`. |
| Issue detail shows start prerequisites and whether each has been delivered | FAIL | API detail includes fields at `packages/cli/src/api/issues.ts:888-907`, but service currently returns empty prerequisite views (`packages/cli/src/services/issue-prerequisite-service.ts:66-69,143-149`), and Web UI hides the section outside backlog at `packages/cli/web/src/components/IssueDetailPage.tsx:816-840`. |
| Issue list or card shows a concise waiting reason | PASS with warning | CLI list renders waiting text from API data at `packages/cli/src/cli/commands/issue.ts:324-326`; web cards render waiting text at `packages/cli/web/src/components/IssueCard.tsx:301-313`. This depends on the broken service projection being fixed. |
| `mo issue start`, API start, and Web UI Start use the same start eligibility guard | FAIL | API start and queue backstop call the service (`packages/cli/src/api/issues.ts:1156-1164`, `packages/cli/src/services/agent-runner-service.ts:1058-1067`), but the guard is ineffective because the service fails to surface persisted prerequisites. |
| If a prerequisite issue has not been delivered, the current issue cannot enter the pipeline | FAIL | Intended guard exists, but focused tests show `evaluateStartEligibility()` returns `startable=true` after declaration (`tests/services/issue-prerequisite-service.test.ts:163-170`). |
| If start is rejected, Mohist does not enqueue `start-pipeline` | PASS with warning | API route returns before enqueue on non-startable result at `packages/cli/src/api/issues.ts:1157-1164`; test coverage exists at `tests/api-issue-prerequisites.test.ts:352-379`. This is only correct once the service correctly reports waiting prerequisites. |
| Once prerequisite issues are delivered, the current issue becomes startable without manual cleanup | FAIL | This behavior is specified in service tests, but the same suite fails because prerequisite projection is broken (`tests/services/issue-prerequisite-service.test.ts:173-185`, `:247-257`). |
| Circular prerequisite declarations are rejected | PASS | Circular check implemented at `packages/cli/src/services/issue-prerequisite-service.ts:177-195`; API returns `reason` at `packages/cli/src/api/issues.ts:1221-1226`; API tests cover it at `tests/api-issue-prerequisites.test.ts:112-143`. |
| Issue-level start prerequisites are not mixed with task-level `tasks.json dependsOn` | PASS | No implementation reads `tasks.json` in the prerequisite service; tests cover separation at `tests/services/issue-prerequisite-service.test.ts:353-399` and CLI at `tests/cli-issue-prerequisites.test.ts:420-454`. |
| API returns structured start prerequisite/status data so frontend does not parse issue body text | PASS with warning | List/detail and start rejection expose `prerequisites`/`startEligibility` at `packages/cli/src/api/issues.ts:528-537`, `888-907`, `1159-1162`, but create responses do not include those fields at `604-610`, which breaks the create-flow criterion in the CLI spec. |

## Verification

- Focused tests failed: `npm test -- --run tests/services/issue-prerequisite-service.test.ts tests/api-issue-prerequisites.test.ts tests/cli-issue-prerequisites.test.ts tests/start-eligibility-queue.test.ts web/src/components/IssueDetailPage-prerequisites.test.tsx`
- Result: 5 test files failed, 42 tests failed, 23 tests passed.

## Overall

Overall result: FAIL. The core prerequisite state is not being projected correctly, the CLI declaration flow is incomplete, and the create/start UX still depends on client-side fallback behavior that the spec explicitly forbids.

<promise>FAIL</promise>
