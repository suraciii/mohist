# Review Report

## Result: PASS

## Repaired Items

(none)

## Blocking Items

(none)

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: packages/runner/src/actions/publish-via-pr.ts:448
  Evidence: `classifyGhFailure` defaults unrecognised stderr/text to `retry-safe`. If `gh` emits a genuinely non-retryable error that does not match any of the five recognised patterns, workflow retry will loop until exhausted — wasting runner resources and clogging the integrate queue. The five-classification spec requirement is satisfied, but the fallback is risky for unanticipated GitHub API errors.
  SuggestedAction: In a follow-up issue, consider adding an `unknown` failure kind that stops retries after a configurable threshold, or a conservative default (e.g. `retry-safe` with a distinct label so an operator can intervene if retries don't converge).
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx:8-17, packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx:43-55
  Evidence: `parseTaskOutput` / `parseTimelineTaskOutput` is duplicated verbatim between `TaskProgressPanel.tsx` and `WorkflowView.tsx`. Both are tiny 8-line helpers, but a third consumer would make the duplication costly. The progress.txt (#141-142) acknowledges this and defers extraction to `shared/lib`.
  SuggestedAction: If a third consumer appears, extract into `packages/web/src/shared/lib/parse-task-output.ts`.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx:532-536
  Evidence: `PrDeliverySummary` is rendered inside `<div className="mb-8">` unconditionally when `workflowTimeline` exists. For `mohist/default` issues the component returns `null`, so the empty `div` with 32px bottom margin still occupies layout space. This is invisible to users but adds a tiny layout gap.
  SuggestedAction: Consider lifting the conditional into the page-level render: `{workflowTimeline && findPublishViaPrMetadata(workflowTimeline) && (<div><PrDeliverySummary/></div>)}` — avoids a rendering a vacuous wrapper. Low priority.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: _output (repository root)
  Evidence: The `_output` file (`<promise>done</promise>`) was added by T-005 as a workflow marker for the build-stage verify check. It is a workflow artifact, not a product deliverable. It does not belong in the product commit tree.
  SuggestedAction: Either `.gitignore` the `_output` file (add `_output` to `.gitignore`) or remove it before merging. Not blocking: per the candidate boundary this is a workflow artifact and is expected to exist during build.
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-pr.workflow.yaml:1-7
  Evidence: The YAML top-level `description` field reads "merge, push" — matching `mohist/default` byte-for-byte. This is deliberate (verified by `PrWorkflowYaml_IsByteIdenticalToDefault_ModuloPublishAction`). The actual PR-specific description lives in `MohistPrIssueWorkflowProfile.PrDescription`. The YAML description is not surfaced to users through any code path.
  SuggestedAction: No action needed. If the YAML description is ever rendered independently of the C# profile, update it in both files.
  Status: pre-existing

## Review Summary

**Alignment**: All 12 issue Acceptance Criteria trace to implemented code with concrete evidence:
1. (profile coexistence) `IssueWorkflowProfileRegistry` registers both profiles — `MohistPrIssueWorkflowProfileSpecs.cs:141-155`
2. (gh prerequisite) `runGhPrecheck` in `publish-via-pr.ts:356-380`, tested in `publish-via-pr.spec.ts:182-238`
3. (plan→build→check→integrate) `mohist-pr.workflow.yaml:276-311` — rebase `squash: false`, publish uses `mohist/publish-via-pr`
4. (idempotency) three-layer: force-with-lease push (`publish-via-pr.ts:140`), PR reuse (`openOrReusePr`, `publish-via-pr.ts:190-247`), already-merged confirmation (`mergeOrConfirmPr`, `publish-via-pr.ts:263-354`) — all tested
5. (failure classification) five kinds in `classifyGhFailure` + per-kind predicates (`publish-via-pr.ts:440-499`); non-retryable kinds surfaced in `delivery-failure.ts:95-112` and `DeliveryFailureGuidance.cs:44-53`
6. (completion signal) `succeed()` returns `status: "completed"` with `prNumber`/`prUrl`/`mergeCommitSha` — `WorkflowGrain.cs` captures `result.Output`
7. (full commit history) `git push --force-with-lease origin <branch>`, no local squash — `publish-via-pr.spec.ts:604-657`
8. (merge commit message) `gh pr merge --squash --subject "Complete issue #N" --body ""` — line 311 and test on line 123
9. (no remote branch deletion) push never uses `--delete`, test verifies `publish-via-pr.spec.ts:652-656`
10. (task result metadata) structured output JSON includes `prNumber`/`prUrl`/`mergeCommitSha` — `publish-via-pr.ts:167-174`
11. (PR indicator) `PrDeliveryIndicator.tsx` renders "经由 PR #N 合并" with link — `PrDeliveryIndicator.test.tsx:17-28`
12. (no CI) no GitHub Actions, webhook, or CI config present — verified by absence

**Test Coverage**: All three layers pass their test suites cleanly:
- Server: 35 profile/registry specs + 67 CLI table-renderer specs — all pass
- Runner: 441 tests (33 files) pass, including 53 publish-via-pr tests covering every step, idempotency path, and failure kind
- Web: 2056 tests (139 files) pass, including new `PrDeliveryIndicator` render tests, `delivery-failure` guidance tests, and `pr-delivery` extractor tests

**Design consistency**: D1–D7 decisions are implemented as designed — parallel YAML resource with a subclass profile, separate runner action file, pure function classifiers, `--force-with-lease` push, PR metadata on existing `TaskRun.Output`, and no action-internal rebase loop.

**Safety**: No new secrets, tokens, or credential storage. `gh` auth lives on the host. No breaking changes to `mohist/default`. No new DB schema.

<promise>PASS</promise>
