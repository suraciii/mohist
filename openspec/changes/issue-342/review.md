# Review Report

## Result: FAIL

## Repaired Items

- _(none)_

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/web/src/pages/issue-detail/ui/MobileActionBar.tsx
  Evidence: The mobile action bar does not honor `decision.primary.enabled`, `decision.primary.reason`, or the source action label. `deriveRuntimeDecision` can return a non-inspect disabled primary fallback when no executable action exists: e.g. queued backlog with a draft/prerequisite/runner blocker produces a disabled Start action (`packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts:214`, `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts:332`, `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts:344`). The desktop surface disables those actions and preserves their reason (`packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx:115`), but the mobile button is disabled only for pending mutations and still calls `invokeAction` on click (`packages/web/src/pages/issue-detail/ui/MobileActionBar.tsx:66`, `packages/web/src/pages/issue-detail/ui/MobileActionBar.tsx:127`). The same component also overwrites `primary.label` with hard-coded labels (`packages/web/src/pages/issue-detail/ui/MobileActionBar.tsx:20`, `packages/web/src/pages/issue-detail/ui/MobileActionBar.tsx:104`), so a failed-state `Start new workflow` action from `deriveRuntimeDecision` (`packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts:266`) is shown as generic `Start`. [disallowed:product-behavior-change]
  SuggestedAction: Render the mobile primary button from the `RuntimeAvailableAction` metadata: use `primary.label` when not pending, disable when `!primary.enabled || pending`, preserve `primary.reason` as title/accessible description, and prevent mutation invocation for disabled actions. Add narrow tests for draft/prerequisite/runner-blocked Start and for the failed-state `Start new workflow` label.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed (4204 passed, 1 skipped), but no test covers disabled primary actions in the mobile bar.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/web/src/pages/issue-detail/ui/MobileActionBar.tsx
  Evidence: Narrow viewports remove `RuntimeDecisionSurface`, which is the only current place that surfaces runtime mutation errors (`packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx:324`). `MobileActionBar` reads pending state but never renders `approveMutation.error`, `sendBackMutation.error`, `retryMutation.error`, `resumeMutation.error`, `rerunMutation.error`, `forceStopMutation.error`, `stopMutation.error`, or `startMutation.error` (`packages/web/src/pages/issue-detail/ui/MobileActionBar.tsx:44`). A failed mobile Start/Approve/Stop/Retry therefore gives no visible error after the action, regressing the existing action surface behavior for the exact urgent mobile flows this issue targets. [disallowed:product-behavior-change]
  SuggestedAction: Add an error region in the mobile action surface/drawer that mirrors the desktop `runtime-action-error` behavior and is reachable by screen readers. Cover at least one immediate action failure and one drawer-confirmed failure in tests.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed (4204 passed, 1 skipped), but no new test asserts mobile mutation error visibility.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/web/src/pages/issue-detail/ui/ConfirmationDrawer.tsx
  Evidence: The custom drawer declares `aria-modal="true"` (`packages/web/src/pages/issue-detail/ui/ConfirmationDrawer.tsx:65`) but does not trap focus, inert background content, or block pointer interaction outside the sheet. The outer fixed dialog container is explicitly `pointer-events-none` (`packages/web/src/pages/issue-detail/ui/ConfirmationDrawer.tsx:69`), and focus management only moves focus to the first focusable child once on open (`packages/web/src/pages/issue-detail/ui/ConfirmationDrawer.tsx:30`). Users can tab or click back into the page while assistive tech is told a modal dialog is active, which is an accessibility contract mismatch for the destructive confirmation flow. [disallowed:architectural-judgment]
  SuggestedAction: Either make the drawer truly modal by trapping focus and inerting/blocking background interaction while keeping the status headline visually readable, or change the semantics to a non-modal dialog pattern and test keyboard traversal explicitly.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed (4204 passed, 1 skipped). Existing drawer tests cover initial focus and Escape only, not focus containment or background interaction.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/web/src/pages/issue-detail/ui/MobileActionBar.tsx
  Evidence: The 768-1024px narrow/no-global-nav band is specified to anchor the bar flush to the bottom edge with no nav footprint reserved (`openspec/changes/issue-342/specs/issue-detail-mobile-action-bar/spec.md:49`). The implemented class uses `md:bottom-[calc(0.5rem+env(safe-area-inset-bottom))]` and the wrapper also has bottom padding (`packages/web/src/pages/issue-detail/ui/MobileActionBar.tsx:117`), so at ~900px the bar is intentionally lifted from the bottom rather than flush. The test only checks that the `md:` value does not contain `3.5rem`, so it misses the flush-bottom part of the scenario. [disallowed:product-behavior-change]
  SuggestedAction: Align the no-nav band with the spec, for example by anchoring the bar container at `md:bottom-0` and keeping only safe-area/content padding if needed, then update the ~900px test to assert the exact no-nav offset contract.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed (4204 passed, 1 skipped), but the existing test does not assert the actual flush-bottom value.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: packages/web/src/pages/issue-detail/ui/IssueDetailPage.narrow-action-bar.test.tsx and packages/web/src/pages/issue-detail/ui/MobileActionBar.test.tsx
  Evidence: The new tests cover happy-path primary actions, drawer copy, padding class presence, and desktop restoration, but they do not cover disabled-primary states, custom primary labels, mutation error display, focus containment/background interaction, or the exact no-nav bottom offset. These are the areas where the implementation diverges from the existing desktop surface and from the spec, as described in item-1 through item-4.
  SuggestedAction: Add regression tests for the uncovered cases before accepting the candidate: blocked/draft/prerequisite Start must be disabled and must not mutate, failed-state Start must preserve `Start new workflow`, mobile action errors must render, drawer keyboard focus must not escape under modal semantics, and the ~900px offset must match the chosen spec value.
  Verification: `npm run test:run -w packages/web` passed (4204 passed, 1 skipped), but passing status is insufficient because the missing assertions permit the regressions above.
  Status: open

## Follow-up Items

- _(none)_

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: workflow artifacts under openspec/changes/issue-342/
  Evidence: The plan/task artifacts still have task `passes: false` in `tasks.json` even though progress and commits indicate T-001/T-002/T-003 completed. These artifacts are workflow context rather than product deliverables under the candidate boundary, and this mismatch does not by itself affect the product snapshot.
  SuggestedAction: Optionally sync task pass flags if the workflow uses them for traceability after review.
  Status: out-of-scope

<promise>FAIL</promise>
