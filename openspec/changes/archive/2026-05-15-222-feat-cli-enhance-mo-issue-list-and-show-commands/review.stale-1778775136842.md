## Findings

1. High: `active` alias does not preserve OR semantics when combined with other stage selectors.
File: `packages/cli/src/api/issues.ts:553-579`
Evidence: the route first filters by the union of parsed stages (`stages.includes(issue.stage)`), then applies a global `isActiveAlias` post-filter that removes every non-pipeline issue and every terminal-status issue whenever `active` appears anywhere in the selector. A query like `stage=active,done` should behave as OR within the stage set, but the current code drops `done` results entirely.
Suggested fix: keep `active` as its own predicate during selection instead of a global boolean. For example, parse into selector terms and include an issue when it matches any selected stage term or the `active` predicate.

2. High: `--attention` misses delivery-blocked / integrate-failed issues that are still in `integrate`.
File: `packages/cli/src/api/issues.ts:63-75`
File: `packages/cli/src/workflow/issue-lifecycle.ts:44-46,58-75`
Evidence: `isAttentionIssue()` relies on `classifyMergeDelivery(issue)` returning `blocked` or `build-failed`, but `classifyMergeDelivery()` returns `integrating` immediately for every issue in `Stage.Integrate` before inspecting `mergeState`. That means integrate-stage issues with `MergeState.Blocked`, `MergeState.BuildFailed`, or `MergeState.Conflict` are not treated as attention items, which conflicts with the spec's delivery-blocked / integrate-failed requirement.
Suggested fix: either evaluate `mergeState` before the `Stage.Integrate` fast path in `classifyMergeDelivery()`, or make `isAttentionIssue()` inspect `issue.mergeState` directly for blocked/failure/conflict states.

3. High: the package currently fails the required typecheck/build gate.
File: `packages/cli/src/api/issues.ts:157,170,439,441,453,638,647,991,1000,2758` and additional lines reported by `tsc`
Evidence: `npm run build` in `packages/cli` fails with unresolved identifiers including `CheckResult`, `getLatestCheckResult`, `WorkflowRunService`, `WorkflowApplicationService`, `isValidModelId`, and `assembleSessionTranscript`, plus related implicit `any` and return-path errors. This means the implementation does not meet the "Typecheck passes" expectation recorded in the task list.
Suggested fix: restore the missing imports/helpers in `packages/cli/src/api/issues.ts`, then rerun `npm run build` until `tsc` passes cleanly.

## Acceptance Criteria

1. PASS: `mo issue list -s active` returns pipeline, non-terminal issues and excludes backlog-active issues.
Evidence: `packages/cli/src/api/issues.ts:28-60,573-579`; tests in `packages/cli/tests/issue-list-filters.test.ts:107-139`; CLI forwarding in `packages/cli/src/cli/commands/issue.ts:252-270` and tests `packages/cli/tests/issue-list-cli.test.ts:57-99`.

2. PASS: `mo issue list -s build,check` returns build or check issues.
Evidence: `packages/cli/src/api/issues.ts:38-60,562-565`; tests `packages/cli/tests/issue-list-filters.test.ts:142-187`; CLI regression `packages/cli/tests/issue-cli-enhancements-regression.test.ts:390-412`.

3. PASS: invalid stage/alias returns a clear error and non-zero exit.
Evidence: API 400 path in `packages/cli/src/api/issues.ts:545-552`; CLI exits in `packages/cli/src/cli/commands/issue.ts:275-278`; tests `packages/cli/tests/issue-list-filters.test.ts:165-186` and `packages/cli/tests/issue-list-cli.test.ts:162-197`.

4. FAIL: stage selection does not fully preserve OR semantics when aliases are combined with other stage filters.
Evidence: `packages/cli/src/api/issues.ts:553-579` applies `isActiveAlias` as a global narrowing step.

5. FAIL: `mo issue list --attention` is incomplete for delivery-blocked / integrate-failed issues.
Evidence: `packages/cli/src/api/issues.ts:70-72` depends on `classifyMergeDelivery()`, but `packages/cli/src/workflow/issue-lifecycle.ts:44-46` forces all integrate-stage issues to `integrating`.

6. PASS: `mo issue list --attention` excludes normal running/probing issues.
Evidence: predicate only includes awaiting approval, blocked/interrupted, and selected delivery states in `packages/cli/src/api/issues.ts:63-75`; tests `packages/cli/tests/issue-list-filters.test.ts:274-283`.

7. PASS: `--attention` composes with stage/priority/label and has an explicit empty state.
Evidence: filtering order in `packages/cli/src/api/issues.ts:581-590`; empty-state output in `packages/cli/src/cli/commands/issue.ts:281-287`; tests `packages/cli/tests/issue-list-filters.test.ts:285-327` and `packages/cli/tests/issue-list-cli.test.ts:143-159,229-249`.

8. PASS: CLI help documents `--attention`, comma-separated status values, and does not expose `--my`.
Evidence: help text in `packages/cli/src/cli/commands/issue.ts:235-240`; tests `packages/cli/tests/issue-list-cli.test.ts:200-227`.

9. PASS: `mo issue show <id> --compact` prints a one-line human-readable summary.
Evidence: `packages/cli/src/cli/commands/issue.ts:345-351`; tests `packages/cli/tests/issue-show-compact.test.ts:36-93`.

10. PASS: compact show omits long sections and skips extra fetches.
Evidence: early return before sessions/executions in `packages/cli/src/cli/commands/issue.ts:345-351`; tests `packages/cli/tests/issue-show-compact.test.ts:95-168`.

11. PASS: default `mo issue show <id>` remains full detail.
Evidence: unchanged full-detail branch in `packages/cli/src/cli/commands/issue.ts:354-509`; tests `packages/cli/tests/issue-show-compact.test.ts:170-227`.

12. PASS: `mo issue diff <id> --stat` prints file-level stats without patch hunks.
Evidence: stat formatter in `packages/cli/src/cli/commands/issue.ts:869-885`; tests `packages/cli/tests/issue-diff-stat.test.ts:81-124`.

13. PASS: default `mo issue diff <id>` remains full patch output.
Evidence: default branch in `packages/cli/src/cli/commands/issue.ts:886-900`; tests `packages/cli/tests/issue-diff-stat.test.ts:143-179`.

14. PASS: default diff and `--stat` share the same comparison semantics.
Evidence: both call `/issues/${number}/diff` in `packages/cli/src/cli/commands/issue.ts:842-845`; test `packages/cli/tests/issue-diff-stat.test.ts:162-178`.

15. PASS: unavailable diff states are distinct and exit non-zero.
Evidence: reason mapping and exit path in `packages/cli/src/cli/commands/issue.ts:854-866`; tests `packages/cli/tests/issue-diff-stat.test.ts:181-240`.

16. PASS: no-change diff is explicit.
Evidence: `packages/cli/src/cli/commands/issue.ts:871-873,897-899`; tests `packages/cli/tests/issue-diff-stat.test.ts:106-124`.

## Verification

- `npx vitest run issue-list-filters.test.ts issue-list-cli.test.ts issue-show-compact.test.ts issue-diff-stat.test.ts issue-cli-enhancements-regression.test.ts` -> PASS
- `npm run build` in `packages/cli` -> FAIL due TypeScript errors in `packages/cli/src/api/issues.ts`

<promise>FAIL</promise>
