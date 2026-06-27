# Review Report

## Result: PASS

Reviewed the post-build candidate snapshot against issue 273, the proposal, design, delta specs, tasks, self-review, all product changes, and adjacent retry/recovery/archive paths. The current snapshot satisfies the requested workflow recovery fixes and I found no unresolved blocking issues.

## Repaired Items

- None in this review pass. The current snapshot already contains the local archive idempotency and coverage repairs needed for the earlier review findings.

## Blocking Items

- None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: packages/server variable namespace / packages/runner/src/actions/openspec.ts
  Evidence: The internal archive destination variable uses the runner-side key `_actions.archiveChange.destination` (`packages/runner/src/actions/openspec.ts:12`). This is safe in the current closed action/profile set, and unsafe persisted archive names are now rejected, but the prefix is still only a convention.
  SuggestedAction: If user-authored workflow variables or custom actions become broader public surface, reserve or validate the `_actions.*` namespace server-side.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: test tooling
  Evidence: `npm test` passes, but the web test run emits a Vitest 4 deprecation warning for `test.poolOptions`. This is unrelated to issue 273 and no web files are part of the candidate product change.
  SuggestedAction: Update the web Vitest config in a separate maintenance change.
  Status: out-of-scope

## Acceptance Criteria Evidence

- AC1/AC2: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-github-pr.workflow.yaml:276` to `:291` no longer declares `conflictMode`, and the inner `when: conflict` handler has no `retrySelf`. `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs:416` to `:427` asserts both absences.
- AC3/AC4: `mohist-github-pr.workflow.yaml:292` to `:300` switches only the base-moved `recover:push` task to `force: true`; `:310` to `:317` keeps the PR-checks recovery push on `forceWithLease: true`. `packages/runner/src/actions/push.ts:24` to `:54` implements `force` winning over `forceWithLease` and emitting bare `--force` without an `ls-remote` probe.
- AC5: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml:272` to `:281` only shares `mohist/archive-change`; it has no matching rebase conflict or recovery push config to sync.
- AC6: `packages/runner/src/actions/openspec.ts:230` to `:241` reads and validates a persisted archive name. `:305` to `:319` resolves the final destination basename, persists that exact basename through `writeVars`, and does so before `moveChangeDir` at `:334`.
- AC7: `packages/runner/src/core/types.ts:88` to `:94` adds `ActionContext.writeVars`, and `packages/runner/src/runtime/executor.ts:578` to `:594` wires it directly to `connection.patchRunVars(workflowRunId, vars, signal)`. `packages/runner/tests/executor-write-vars.spec.ts:21` to `:56` verifies the write happens immediately even when the action later fails.
- AC8: Coverage was updated in `packages/runner/tests/push.spec.ts`, `packages/runner/tests/openspec.spec.ts`, `packages/runner/tests/executor-write-vars.spec.ts`, and `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs`.

## Verification

- `npm run typecheck -w packages/runner`: passed.
- `npm test -w packages/runner`: passed, 664 passed / 23 skipped.
- `npm test`: passed. Server: 2785 passed / 12 skipped. Web: 2376 passed / 1 skipped. Runner: 664 passed / 23 skipped.

<promise>PASS</promise>
