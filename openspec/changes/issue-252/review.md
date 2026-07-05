# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: cleanup | spec-compliance
  Evidence: `packages/web/src/widgets/issue-workflow/ui/InlineApproval.tsx` still duplicated the script-health predicate inline at `StepList` instead of consuming the extracted shared helper from `model/runtime-query-helpers.ts`. Replaced the inline predicate with `checkResults.filter(isScriptHealthCheck)` and added the import. This keeps `isScriptHealthCheck` as the single implementation while preserving behavior for `StageCheckState` (`checkName === 'health'` or `output.kind === 'script'`).
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed with 295 files / 4407 tests passed and 1 skipped; grep confirms only `runtime-query-helpers.ts` defines the helper and `InlineApproval.tsx` imports/calls it.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: test suite
  Evidence: The web Vitest run reports 1 skipped test while all 4407 executed tests pass. No test files are changed by this candidate, and the issue spec treats the existing suites as the regression oracle for this structural refactor.
  SuggestedAction: Track the skipped test separately if it is not already intentional.
  Status: out-of-scope

## Acceptance Criteria Evidence

- WorkflowView decomposition: `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx` is now 66 lines and only composes `StageBar`, `SpecialStatePanel`, `StepList`, and `IntegrateFailurePanel`; moved symbols live in the requested sibling files (`format.ts`, `StageStatusIcons.tsx`, `StageBar.tsx`, `TaskItem.tsx`, `CheckItem.tsx`, `InlineApproval.tsx`, `failure-panels.tsx`).
- Runtime decision presentation: `packages/web/src/widgets/issue-workflow/model/runtime-presentations.ts` contains `PRESENTATIONS: Record<RuntimeSummary, SummaryPresentation>` at line 207, with each summary's headline/rationale/nextAction/actions co-located; `derive-runtime-decision.ts` no longer contains the old per-builder `buildHeadline`/`buildRationale`/`buildNextAction`/`buildActions` summary walks.
- Shared query helpers: `packages/web/src/widgets/issue-workflow/model/runtime-query-helpers.ts` owns the extracted helper implementations, and UI/model consumers import them after the repair.
- Public barrel: `packages/web/src/widgets/issue-workflow/index.ts` is unchanged against `master`, so widget barrel exports remain stable.
- Regression gates: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed.

<promise>PASS</promise>
