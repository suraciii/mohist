# Review Report

## Result: PASS

All blocking findings from the previous review pass have been resolved in the
current post-build candidate snapshot. The change delivers every acceptance
criterion in the spec, the relevant new tests pass (10/10 in
`WorkflowView.test.tsx`, 9/9 in `WorkflowArtifacts.test.tsx`, 2/2 in the new
`tests/markdown-content.test.tsx`), the production build is clean, and the
13 pre-existing failures in unrelated files are not introduced by this change
(verified below).

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: HTML semantics + event-handler guard
  Evidence: The previously-reported nested `<button>` defect is gone.
  `TaskArtifactSummaryChip` is now a `<span role="button" tabIndex={0}>` (no
  real `<button>` element), so the surrounding parent `Button` no longer
  contains a nested interactive element. The chip's `onClick` wraps
  `event.stopPropagation()` before invoking `onClick`, and the keyboard
  handler does the same for `Enter` / `Space`. The pre-existing
  `TaskSessionChip` `event.stopPropagation()` pattern is matched.
  Verification:
  - `cd packages/web && npx vitest run --reporter=verbose src/widgets/issue-workflow/ui/WorkflowView.test.tsx` — 10/10 pass, no nested-button stderr.
  - `cd packages/web && grep -i "nested\|cannot contain\|validateDOMNesting" /tmp/test-output.log` — no occurrences.
  - New regression test `does not toggle the task row when a chip is clicked on an expand-capable task` (`WorkflowView.test.tsx:299-316`) explicitly asserts that the task body is not expanded when the chip is clicked on a task with `message` output, and that the dialog is still opened.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: Same as item-1 (covered by the same fix; tracked separately for clarity)
  Evidence: `event.stopPropagation()` is now called on both `onClick` and
  `onKeyDown` (Enter/Space), so the parent `Button` expand handler is no
  longer triggered by chip activation.
  Verification: same tests as item-1; new keyboard test
  `activates the artifact chip with Enter and Space keyboard events`
  (`WorkflowView.test.tsx:318-327`) confirms Enter activation; the chip code
  branch for `event.key === ' '` (`WorkflowView.tsx:386`) is exercised by the
  shared code path.
  Status: resolved

- [ID: item-3 (from previous review — timer cleanup)]
  Severity: info
  Scope: `ArtifactContentViewer.tsx:60-91, 122-128`
  Evidence: `copyResetTimerRef` is declared, `clearCopyResetTimer` is called
  from `setCopyStatusWithReset`, the dialog `onOpenChange` close branch, and
  the unmount `useEffect` cleanup. No timer can survive a re-open or
  unmount.
  Verification:
  - `cd packages/web && npx vitest run --reporter=verbose src/widgets/issue-workflow/ui/WorkflowArtifacts.test.tsx` — 9/9 pass, no timer leak warnings.
  Status: resolved

- [ID: item-4 (from previous review — formatBytes)]
  Severity: info
  Scope: `ArtifactContentViewer.tsx:17-24`
  Evidence: `formatBytes` now has explicit KB / MB / GB / TB branches and a
  `!Number.isFinite(bytes) || bytes < 0` guard returning `'0 B'`. Negative,
  NaN, and Infinity inputs no longer produce `-X B`, `NaN B`, or
  `Infinity.0 MB`.
  Verification: code review of the new function body; build passes
  (`npm run build` in `packages/web` — 2527 modules transformed, no errors).
  Status: resolved

- [ID: item-5 (from previous review — test gaps)]
  Severity: info
  Scope: `WorkflowView.test.tsx`, `WorkflowArtifacts.test.tsx`, new `packages/web/tests/markdown-content.test.tsx`
  Evidence: All previously-identified test gaps are filled. The new tests
  cover: (a) chip click on an expand-capable completed task does not expand
  the row, (b) `Enter` keyboard activation, (c) `.markdown` extension
  detection (not just `.md`), (d) `size === null` fallback to
  `'Recorded artifact content'`, (e) `navigator.clipboard` missing fallback
  to `'Unable to copy'`, and (f) smoke tests for the shared
  `MarkdownContent` component (headings, lists, GFM tables).
  Verification:
  - `cd packages/web && npx vitest run --reporter=verbose src/widgets/issue-workflow/ui/WorkflowView.test.tsx` — 10/10.
  - `cd packages/web && npx vitest run --reporter=verbose src/widgets/issue-workflow/ui/WorkflowArtifacts.test.tsx` — 9/9.
  - `cd packages/web && npx vitest run tests/markdown-content.test.tsx` — 2/2.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-F1]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/WorkflowView.test.tsx:318-327`
  Evidence: The "activates the artifact chip with Enter and Space keyboard
  events" test description promises both keys but only exercises `Enter`.
  The chip's `onKeyDown` (`WorkflowView.tsx:386-390`) explicitly handles
  `event.key === ' '` and `event.key === 'Enter'`, so a Space-key test would
  catch a future regression where the Space branch is accidentally dropped.
  SuggestedAction: Extend the test to fire `fireEvent.keyDown(chip, { key: ' ' })` (or parametrise) and assert the dialog opens.
  Status: follow-up

- [ID: item-F2]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/WorkflowArtifacts.test.tsx:366-409`
  Evidence: The "Unable to copy" test mutates `navigator.clipboard` via
  `Object.defineProperty` and restores in `finally`. While the test does
  pass and the restoration works in jsdom, a safer pattern for a global
  fixture like this is `vi.spyOn(navigator, 'clipboard', 'get')` (or
  per-test setup in a `beforeEach`/`afterEach`). The current approach is
  correct but the restoration depends on `configurable: true`; if jsdom
  changes that property descriptor in a future vitest upgrade, the test
  could start leaking clipboard state into other tests.
  SuggestedAction: Switch to `Object.defineProperty(navigator, 'clipboard', { configurable: true, get: () => undefined })` in `beforeEach` and a real `afterEach` that deletes the override; or use `vi.stubGlobal('navigator', { ... })` patterns.
  Status: follow-up

- [ID: item-F3]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/ArtifactContentViewer.tsx:115-119`
  Evidence: When the viewer is open for a file artifact whose content is
  still loading (`isLoading === true`), the size label correctly falls back
  to either the prop `size` or `'Recorded artifact content'`. However, when
  the load fails (`error` is set), the size label is still rendered with the
  same fallback even though the artifact may genuinely have a different
  size. The behaviour is acceptable but could be more explicit by hiding
  the size line under the error banner.
  SuggestedAction: Optionally render the size label only when `!error` (or
  rename the label under error to e.g. `'Unavailable'`). Not a defect;
  existing tests do not exercise the error + size interaction.
  Status: follow-up

- [ID: item-F4]
  Severity: follow-up
  Scope: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`
  Evidence: The `MarkdownContent` component was extracted from this file
  (T-001) but no test for the issue-detail markdown rendering was added.
  The existing test at `src/pages/issue-detail/` (3/3 pass) does not assert
  that issue-body markdown renders. If a future regression changes the
  shared component's behaviour, the issue-detail surface could break
  silently.
  SuggestedAction: Add a small assertion in the issue-detail test file that
  renders an issue with a markdown body and verifies a heading element is
  produced.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-P1]
  Severity: info
  Scope: `tests/canonical-event-types.test.ts`, `tests/useCoderSessions.test.tsx`, `tests/live-task-cloud-event.test.tsx`, `src/widgets/app-shell/ui/Header.test.tsx`, `src/pages/epics/ui/EpicListPage.test.tsx`
  Evidence: 13 tests in these 5 files fail on the current candidate
  snapshot. Verifying them as pre-existing:
  - `cd packages/web && npm run test:run 2>&1 | tail -3` reports
    `Test Files  5 failed | 50 passed (55)` and
    `Tests  13 failed | 851 passed (864)`.
  - None of the failing files is touched by this change (verified with
    `git status --porcelain`).
  - Failure messages reference `TRANSCRIPT_EVENT_TYPES` shapes
    (`session.input`, `message.delta`, `tool_call.started` etc.),
    `agent_usage_update` routing, and missing `<h1>` headings on
    `/epics`, `/activity`, `/logs` routes — all unrelated to artifact
    rendering.
  SuggestedAction: Out of scope for issue 98. Address in a separate change.
  Status: pre-existing

- [ID: item-P2]
  Severity: info
  Scope: `packages/web/src/widgets/issue-workflow/ui/LatestArtifactsPanel.tsx:37-39`
  Evidence: The panel still renders only `(artifact.entries?.length ?? 0) files`
  for directory artifacts, not `totalSize`. The change correctly fixed this
  in the viewer header (REQ-WAUX-007) but the latest-artifacts list on the
  issue page is a different surface and was not part of the spec.
  SuggestedAction: Out of scope; consider as a follow-up if the issue-page
  artifact list should also surface size.
  Status: out-of-scope

## Spec Compliance Check

- REQ-WAUX-001 (chip on task row for completed tasks) — implemented at
  `WorkflowView.tsx:465-475`; visibility gate is
  `task.status === 'completed' && hasArtifacts`; tests cover
  completed-with (`WorkflowView.test.tsx:239-248`), directory chip
  (`:250-258`), running task (`:260-268`), and completed-without
  (`:270-278`).
- REQ-WAUX-002 (chip click opens viewer) — implemented at
  `WorkflowView.tsx:471, 521` via `setSelectedArtifact(summary)`; new
  test `opens ArtifactContentViewer when an artifact chip is clicked`
  (`:280-297`) asserts dialog appears with the right `artifactId`,
  plus new test `does not toggle the task row when a chip is clicked on
  an expand-capable task` (`:299-316`) ensures the click does not
  toggle the row.
- REQ-WAUX-003 (markdown rendering for `.md`/`.markdown`) — implemented
  at `ArtifactContentViewer.tsx:26-30, 191-194`; new test
  `renders .markdown artifacts as markdown (not as <pre> text)`
  (`WorkflowArtifacts.test.tsx:310-338`) exercises the `.markdown`
  extension branch; the new `tests/markdown-content.test.tsx` covers
  the shared component.
- REQ-WAUX-004 (non-markdown `<pre>`) — implemented at
  `ArtifactContentViewer.tsx:195-199`; covered by the existing
  `opens recorded artifact content when latest artifact is clicked`
  test (`WorkflowArtifacts.test.tsx:195-220`) which renders a
  non-markdown file path implicitly through the original fixture.
- REQ-WAUX-005 (file size in header) — implemented at
  `ArtifactContentViewer.tsx:113-119`; covered by the same test which
  asserts `'123 B'` is displayed.
- REQ-WAUX-006 (copy feedback) — implemented at
  `ArtifactContentViewer.tsx:60-107, 146-155`; new test
  `shows "Unable to copy" feedback when navigator.clipboard is
  unavailable` (`WorkflowArtifacts.test.tsx:366-409`) covers the
  error branch; the success branch follows the same code path.
- REQ-WAUX-007 (directory total file count + total size) —
  implemented at `ArtifactContentViewer.tsx:115-117, 168-173`;
  covered by `renders directory entries and opens contained file
  content` (`WorkflowArtifacts.test.tsx:222-262`).

All seven requirements are met with concrete evidence (file paths,
line numbers, and passing tests).

## Verification Summary

- `cd packages/web && npx tsc -b` — exit 0, no TypeScript errors.
- `cd packages/web && npm run build` — 2527 modules transformed, build
  succeeds.
- `cd packages/web && npx vitest run src/widgets/issue-workflow/ui/WorkflowView.test.tsx` — 10/10 pass, no nested-button stderr.
- `cd packages/web && npx vitest run src/widgets/issue-workflow/ui/WorkflowArtifacts.test.tsx` — 9/9 pass.
- `cd packages/web && npx vitest run tests/markdown-content.test.tsx` — 2/2 pass.
- `cd packages/web && npx vitest run src/pages/issue-detail` — 3/3 pass
  (regression check for the `MarkdownContent` extraction).

<promise>PASS</promise>
