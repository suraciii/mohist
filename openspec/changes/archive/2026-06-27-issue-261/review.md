# Review Report

## Result: FAIL

## Repaired Items

_None._ No safe local repairs were made during review.

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:488
  Evidence: The issue's data definition says per-stage rework rate denominator is "进入某 stage 的 issue" / "进入过该 stage 的 issue 数", but `GetQualityAsync` only iterates issues whose status is `Done` and only buckets by `work-completed` ship time before accumulating stage denominators. This means an in-progress issue that has entered `plan` and triggered repair is excluded from the `plan` denominator and numerator until it ships, even though the issue-level acceptance data definition counts stage-entered issues. The local openspec narrows this to shipped-in-window issues, but that narrowing changes the product metric requested in the issue rather than simply implementing it. [disallowed:product-behavior-change]
  SuggestedAction: Confirm the intended product contract. If the issue text is authoritative, change stage rework aggregation to count all issues that entered each stage for the trailing windows using an explicit stage-entry time anchor, and keep first-time-right limited to shipped issues. If shipped-only stage rates are intended, update the issue/spec acceptance language so the denominator is unambiguous.
  Verification: Add a querier test with one shipped clean `plan` issue and one in-progress repaired `plan` issue; the current implementation reports `plan.enteredCount == 1` and `reworkRate == 0.0`, while the issue definition implies `enteredCount == 2` and `reworkRate == 0.5`.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: packages/web/src/pages/dashboard/productivity/QualityPanel.tsx:100
  Evidence: When both windows are zero-sample, the panel-level empty state hides the per-window structure entirely, so users cannot see that both 7d and 30d windows independently have no shipped samples. This is not a correctness blocker because the empty state is distinguishable from a perfect score and the partial-empty case is tested.
  SuggestedAction: Consider rendering both window headings with empty rows even when both windows are empty, matching the endpoint shape more directly.
  Status: follow-up

## Pre-existing or Out-of-scope Items

_None._

## Verification

- `mo issue show 261 --project-id proj_f6c141d63b6243bfbb481737b2243b87` was read before review.
- Read review context under `openspec/changes/issue-261/`: `proposal.md`, `design.md`, `tasks.json`, `self-review.md`, and both delta specs.
- Reviewed changed product files across server aggregation/routes/DTOs/tests and web hook/panel/dashboard/tests.
- Ran `npm test` from the repository root: passed (`47` runner test files passed, `650` tests passed, plus .NET/server test phase completed before runner output in the combined root script).
- Ran `npm run typecheck -w packages/web`: passed.
- Ran `npm run test:run -w packages/web`: passed (`170` files, `2427` tests passed, `1` skipped).

<promise>FAIL</promise>
