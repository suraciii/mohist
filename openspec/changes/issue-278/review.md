# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/pages/epic-detail/model/advancement.ts`
  Evidence: `deriveAdvancementState` chooses the display candidate with `const candidate = undelivered[0]` (`packages/web/src/pages/epic-detail/model/advancement.ts:63`), but the API sends detail linked issues in link-created order (`packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:224`-`227`) while server next-issue selection and next-reason selection use priority rank then issue number (`packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs:61`-`66`, `79`-`83`). This can render the wrong draft/external/has-next state, or show a contradictory blocker under a server-provided `progress.nextIssue`, when an older linked issue is not the highest-priority candidate. [disallowed:product-behavior-change]
  SuggestedAction: Derive the candidate using the same display ordering contract as the server (`PriorityRank(priority)` then `number`) or use `progress.nextIssue`/`progress.nextIssueReason` as inputs so the UI cannot disagree with the API candidate.
  Verification: Add unit tests where linked order differs from priority/number order, including a server `nextIssue` present while the first linked issue is draft-blocked, then run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`
  Evidence: The new selector labels Pause and Resume as the single primary lifecycle action, but the rendered buttons use `variant="outline"` (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:617`-`638`), the same secondary treatment as Edit/Close. This does not satisfy the issue/spec requirement to突出/primary-highlight the lifecycle action for `running` and `paused` epics. [disallowed:product-behavior-change]
  SuggestedAction: Render the selected primary lifecycle action with the default/prominent button variant consistently for Start Epic, Pause, Resume, and Mark Done; keep Edit/Close and disabled Mark Done secondary.
  Verification: Add DOM/class or visual assertions for running and paused epics that distinguish the primary action from secondary actions, then run the web typecheck/test suite.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`
  Evidence: For a non-terminal epic with no linked issues, `readyToMarkDone` is false by server definition (`packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs:37`) and `unfinishedCount` becomes `0` (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:505`). The disabled Mark Done reason then renders `0 linked issues remain unfinished.` (`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:519`-`521`), which is visible on touch devices but does not explain why the Epic cannot be completed. [disallowed:product-behavior-change]
  SuggestedAction: Add a zero-linked-issues reason such as requiring at least one linked issue before marking done, and cover it with an EpicDetailPage test.
  Verification: Render an idle/running epic with `totalIssueCount: 0`, `deliveredCount: 0`, `readyToMarkDone: false`, assert the disabled reason is actionable and not a zero-unfinished contradiction, then run `npm run test:run -w packages/web`.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx`
  Evidence: The acceptance criteria require the summary to be visible in the first fold on desktop and at a mobile viewport. The added tests assert DOM order only; the mobile test name says `390px viewport`, but no viewport dimensions or layout measurement are set, and jsdom cannot prove first-fold visibility. The implementation likely improves hierarchy by moving the description after the summary, but the specific first-fold/mobile acceptance criterion is not verified.
  SuggestedAction: Add a browser-level or component-layout check for a 390px viewport and a desktop viewport that confirms the progress, current activity, and next issue/reason summary elements are visible before the description consumes the fold.
  Verification: Run the browser/layout test plus `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`
  Evidence: No security, persistence, migration, or public API contract changes were found in the product candidate. The change is web-only and uses existing API fields/mutation hooks.
  SuggestedAction: No action required for this issue.
  Status: out-of-scope

## Verification

- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 173 files, 2559 passed, 1 skipped.

<promise>FAIL</promise>
