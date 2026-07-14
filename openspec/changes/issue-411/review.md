# Review Report

## Result: FAIL

The CSS changes preserve the intended compact structure: `SessionDetailShell` keeps identity, status, and stage visible while reducing metadata, `SessionUsageSummary` becomes denser, and the transcript has a compact-only 120px floor. `npm run typecheck -w packages/web`, `npm run test:run -w packages/web` (334 files, 4,665 tests), and `npm run test:browser -w packages/web` (49 Chromium tests) pass. The candidate cannot pass because recovery-error behavior and required compact control states are not correctly covered.

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/web/src/pages/session/ui/SessionDetailShell.tsx`
  Evidence: The new always-horizontal recovery row makes the `SessionRecoveryActions` wrapper `shrink-0` at line 189. When a Compact request fails, its inline error is a sibling of the buttons with no width bound or wrapping constraint (`packages/web/src/widgets/coder-session/ui/SessionRecoveryActions.tsx:211-220`). Its intrinsic width can consume the 320px row, squeezing the health surface to zero or overflowing it. This failure path existed before, but changing the row from a compact-only column to `flex-row` at line 179 introduces the compact layout regression. No browser test triggers an inline recovery error.
  SuggestedAction: Keep failure output constrained to the available compact width, preferably outside the horizontal health/actions row, and add a 320px browser test that triggers a representative recovery API error and checks document width plus button and error bounds.
  Verification: At `320x568`, make Compact return a 409 or a long server error; assert no document horizontal overflow, the error is readable, and Compact/Reset remain reachable above the mobile navigation.
  Status: unresolved

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/web/tests/browser/coder-session-compact-viewport.spec.ts`
  Evidence: The two tests labelled as running-session composer checks pass an `active` list item at lines 360-365 and 382-387, but the metadata endpoint always returns the hard-coded failed metadata at lines 230-232. `isRunning` is derived from the metadata status (`packages/web/src/pages/session/data/useIssueSessionDataSource.tsx:232-235`), so `SessionDetailShell` renders the short disabled composer (`packages/web/src/pages/session/ui/SessionDetailShell.tsx:390-397`), not the active form. The checks at lines 371-377 and 393-399 therefore do not validate the running composer required by the issue.
  SuggestedAction: Parameterize the metadata fixture with the same active status as the session fixture and assert the active input and Send button are present before measuring the composer at both compact viewports.
  Verification: Run the focused Playwright spec after asserting `session-followup-input` and `session-followup-send` are visible and their bounding box stays above the navigation at `375x667` and `320x568`.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/web/tests/CoderSessionCompactViewport.spec.tsx` and `packages/web/tests/browser/coder-session-compact-viewport.spec.ts`
  Evidence: The structural test named for the cancel trigger at lines 368-405 never queries `session-cancel-trigger`. Its issue-session data source always returns `cancel: null` (`packages/web/src/pages/session/data/useIssueSessionDataSource.tsx:322`), so the issue-route browser fixture cannot render a cancel control. The shared shell does render Cancel for running generic sessions when a cancel mutation is supplied (`packages/web/src/pages/session/data/useGenericSessionDataSource.ts:159-184`, `packages/web/src/pages/session/ui/SessionDetailShell.tsx:541-675`), but no compact browser test covers it. This leaves the explicit cancel-control acceptance scenario unverified.
  SuggestedAction: Add a generic running-session browser fixture at both target viewports and assert the actual cancel trigger is visible, inside the viewport, and above the mobile navigation. Make the structural test inject a non-null cancel dependency and assert the trigger rather than status and stage alone.
  Verification: Run the new generic-session browser test at `375x667` and `320x568`, including a click that opens the confirmation dialog without navigation intercepting it.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/web/tests/browser/coder-session-compact-viewport.spec.ts`
  Evidence: At the critical `320x568` viewport the test accepts any height greater than zero (lines 290-301), even though the implementation promises a 120px floor (`packages/web/src/pages/session/ui/SessionDetailShell.tsx:357-360`) and the issue requires a readable, scrollable transcript. Neither target viewport asserts `scrollHeight > clientHeight`, scrolls the transcript, nor proves header, usage, and composer stay fixed while it scrolls. A one-pixel strip or a non-scrollable transcript would pass this suite.
  SuggestedAction: Assert the readable floor at `320x568`, use sufficiently long transcript data, and verify independent scrolling and fixed surrounding regions at both required viewports.
  Verification: Assert a transcript height of at least 120px, `scrollHeight > clientHeight`, successful `scrollTop` movement, and unchanged header/usage/composer positions after scrolling.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: `packages/web/tests/browser/coder-session-compact-viewport.spec.ts`
  Evidence: The new browser suite uses four fixed `page.waitForTimeout(150)` calls at lines 285, 299, 411, and 431. This conflicts with the repository test rule forbidding real-time waits (`design/testing.md:53-59`) and does not synchronize with the actual layout state.
  SuggestedAction: Remove the wall-clock waits and wait on the rendered transcript, expected text, or a deterministic layout-ready condition before collecting bounding boxes.
  Verification: The focused browser suite passes repeatedly with no `waitForTimeout` calls.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

None.

<promise>FAIL</promise>
