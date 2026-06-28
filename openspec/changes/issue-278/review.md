# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/pages/epic-detail/model/advancement.ts`, `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`
  Evidence: `deriveAdvancementState` checks `externalPrerequisites.length > 0` before `isStartableCandidate` at `packages/web/src/pages/epic-detail/model/advancement.ts:86`, so any candidate with an external prerequisite record is classified as `external-prerequisite-blocker` even when `canStart === true` and `startBlocker === null`. The backend treats `ExternalPrerequisites` as descriptive out-of-epic prerequisite metadata (`packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:256`) and uses `CanStart`/`StartBlocker` as the actual blocker decision (`packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:246`). When the server also returns `progress.nextIssue`, the UI renders the startable next issue link and then renders blocker copy below it via `NextIssueAdvancementCopy` at `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:717`. This violates the acceptance criterion that an idle epic shows whether a startable next issue exists, and only explains a reason when no startable issue exists. [disallowed:product-behavior-change]
  SuggestedAction: Classify a candidate as external-prerequisite-blocked only when it is not startable, or derive the blocker from `canStart`/`startBlocker` first and use `externalPrerequisites` only to enrich the reason/link targets. Add unit and page tests for a candidate with `canStart: true`, `startBlocker: null`, and non-empty `externalPrerequisites`, with `progress.nextIssue` present, asserting that no external-blocker copy is rendered.
  Verification: Run `npm run typecheck -w packages/web`, `npm run test:run -w packages/web -- src/pages/epic-detail/model/advancement.test.ts src/pages/epic-detail/ui/EpicDetailPage.test.tsx`, and the focused Playwright spec after the fix.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/pages/epic-detail/model/advancement.ts`, `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`
  Evidence: `isUndelivered` excludes cancelled linked issues at `packages/web/src/pages/epic-detail/model/advancement.ts:26`, causing an epic whose linked issues are cancelled but not delivered to become `nothing-pending`; `advancementCopy` then displays `All linked issues are delivered` at `packages/web/src/pages/epic-detail/model/advancement.ts:144`. The server does not count cancelled issues as delivered (`packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs:14`) and still includes them in `TotalIssueCount` (`packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs:32`), so the page can show `0 / 1`, disabled Mark Done with `1 linked issue remains unfinished.`, and `All linked issues are delivered` at the same time. [disallowed:product-behavior-change]
  SuggestedAction: Do not use delivered wording for `nothing-pending` unless `progress.readyToMarkDone` or delivered counts prove it. Either keep cancelled issues out of advancement but use neutral copy such as no pending startable linked issues, or pass progress readiness/counts into the copy decision. Add tests for all-cancelled and mixed done/cancelled linked issues.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web -- src/pages/epic-detail/model/advancement.test.ts src/pages/epic-detail/ui/EpicDetailPage.test.tsx` after adding the cancelled-status cases.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/web/src/pages/epic-detail/model/advancement.test.ts`, `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx`
  Evidence: The new advancement tests encode the two incorrect edge cases instead of guarding against them: `packages/web/src/pages/epic-detail/model/advancement.test.ts:77` asserts all-cancelled as `nothing-pending`, while `packages/web/src/pages/epic-detail/model/advancement.test.ts:108` only covers external prerequisites on a non-startable candidate and never covers the common startable candidate with historical/delivered external prerequisite metadata. This leaves the acceptance-critical next issue / reason behavior unprotected.
  SuggestedAction: Add regression tests for startable candidates with external prerequisite metadata and for cancelled linked issues before changing the implementation, so the corrected UI contract is locked down.
  Verification: Run `npm run test:run -w packages/web -- src/pages/epic-detail/model/advancement.test.ts src/pages/epic-detail/ui/EpicDetailPage.test.tsx`.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/pages/epic-detail/model/primaryLifecycleAction.ts`
  Evidence: `primaryActionKind` and `isPrimaryActionKind` are exported and unit-tested at `packages/web/src/pages/epic-detail/model/primaryLifecycleAction.ts:45`, but the product code only consumes `primaryLifecycleAction`. This adds a small amount of public surface and test noise without serving the feature.
  SuggestedAction: Remove the unused exported helpers unless another consumer is expected soon.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: `packages/web` test configuration
  Evidence: Vitest reports `test.poolOptions` was removed in Vitest 4 during both focused and full test runs. This is a repo configuration warning, not caused by the Epic detail candidate.
  SuggestedAction: Update the Vitest config to the Vitest 4 pool option shape in a separate cleanup.
  Status: pre-existing

## Verification

- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web -- src/pages/epic-detail/model/advancement.test.ts src/pages/epic-detail/model/primaryLifecycleAction.test.ts src/pages/epic-detail/ui/EpicDetailPage.test.tsx` passed: 196 tests.
- `npm run test:run -w packages/web` passed: 173 files, 2564 passed, 1 skipped.
- `npm run test:e2e -w packages/web -- tests/e2e/epic-detail-mobile-overflow.spec.ts` passed: 14 tests.
- `git diff --check` passed.

<promise>FAIL</promise>
