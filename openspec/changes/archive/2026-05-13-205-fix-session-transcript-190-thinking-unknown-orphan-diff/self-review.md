## Self Review

Reviewed `proposal.md`, `design.md`, and `tasks.json` against alignment, completeness, consistency, feasibility, and dependency completeness.

Fixes applied during self-review:

- Added change-local spec files for `agent-session-ui`, `pipeline-session-events`, and `coder-session-tracking` so the change has explicit requirement coverage.
- Updated `tasks.json` so every task now references a concrete requirement instead of leaving `spec` empty.
- Added `version: 1` to `tasks.json` for consistency with other change artifacts.
- Updated `design.md` to reference the new change-local specs instead of claiming the change had no specs.

Post-fix check summary:

- Proposal coverage matches the issue scope: inline thinking order, live thinking SSE, tool-id correlation, diff-first rendering, and the known review regressions are all represented.
- Specs now cover every requirement area called out by the proposal and issue acceptance criteria.
- Every task maps to a concrete requirement and the dependency graph remains acyclic with all non-first tasks using `dependsOn`.
- Design decisions remain feasible and consistent with the updated specs and task sequence.

<promise>PASS</promise>
