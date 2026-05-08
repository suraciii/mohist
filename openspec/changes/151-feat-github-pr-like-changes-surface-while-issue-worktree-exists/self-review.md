## Self Review

Reviewed generated artifacts for alignment, completeness, consistency, feasibility, and dependency correctness.

Findings fixed during review:

- Added missing spec delta files for `issue-review-surface`, `http-api`, and `web-ui` so proposal capabilities are covered by specs.
- Revised `tasks.json` so every declared capability has implementation coverage.
- Split frontend work into separate deliverable tasks for the top Issue Detail review summary and the PR-like Changes panel.
- Updated task spec references to point at requirements that now exist in the change specs.
- Revalidated the task dependency graph: `T-001 -> T-002 -> T-003 -> T-004`, with `T-004` depending on all implementation outputs it tests. No cycles or forward dependencies remain.
- Validated `tasks.json` parses as JSON.

All review criteria pass after fixes.

<promise>PASS</promise>
