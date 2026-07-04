# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:66
  Evidence: The issue/spec/task contract says the download filename must match `task-logs-<taskId>-YYYY-MM-DD.txt` (`openspec/changes/issue-338/specs/task-log-viewer/spec.md:53`, `openspec/changes/issue-338/tasks.json:16`). The candidate sanitizes `taskId` with `taskId.replace(/[^A-Za-z0-9._-]/g, '-')` before building the filename, so a valid existing task id such as `integrate:prepare` or `recover:open-pr` becomes `task-logs-integrate-prepare-YYYY-MM-DD.txt` / `task-logs-recover-open-pr-YYYY-MM-DD.txt` instead of preserving the task id. Task ids with `:` are already treated as real ids in adjacent code (`packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx:185`). [disallowed:public-contract-change]
  SuggestedAction: Either preserve the task id in the filename to satisfy the current contract, or explicitly change the issue/spec acceptance to define sanitized filenames and add examples for colon-containing task ids.
  Verification: Add/update a download test using `taskId="integrate:prepare"`, then run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web -- src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx`.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx:817
  Evidence: The new download tests only use `taskId="build-task-1"`, so they cannot catch the filename mismatch for realistic task ids containing `:` even though the spec requires `task-logs-<taskId>-YYYY-MM-DD.txt`.
  SuggestedAction: Add a focused test that renders `TaskLogPanel` with a colon-containing task id and asserts the exact offered filename required by the accepted contract.
  Verification: Run `npm run test:run -w packages/web -- src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx`.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx:1017
  Evidence: The acceptance criteria require scroll-aware auto-follow to pause for new/filtered lines while away from bottom and resume when scrolled near bottom. The tests only cover a live-appended line while paused; they do not cover a filter change while paused, and they do not cover the resume-near-bottom path.
  SuggestedAction: Add tests that fire a paused scroll, change the search/filter, and assert no forced scroll; then fire a near-bottom scroll and assert the next visible-line change follows the bottom.
  Verification: Run `npm run test:run -w packages/web -- src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx`.
  Status: open

- [ID: item-4]
  Severity: minor
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:312
  Evidence: The task log controls are placed in a non-wrapping header row (`flex items-center` with `ml-auto flex items-center`) and the search input has no responsive width constraint. In the narrow task cards where `TaskLogPanel` is rendered (`TaskProgressPanel.tsx:172`), the label, search box, and Download button can exceed the available width instead of wrapping like the system Logs page control row. This weakens the required visual/interaction parity and risks mobile overflow. [disallowed:product-behavior/layout-change]
  SuggestedAction: Move the controls into a wrapping row or add responsive `min-w-0`/width constraints so the search input and button fit within narrow task panels.
  Verification: Run the TaskLogPanel tests and inspect the panel at a narrow viewport or container width.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

_None._

<promise>FAIL</promise>
