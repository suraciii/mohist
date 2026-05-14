## Self Review

Reviewed `proposal.md`, `design.md`, `tasks.json`, and the change-local spec deltas for alignment, completeness, consistency, feasibility, and dependency correctness.

Findings fixed during review:

- Added the missing change-local spec delta files for `http-api`, `issue-changed-files-reader`, `issue-review-surface`, and `web-ui` so every capability declared in the proposal is now covered by specs.
- Updated `tasks.json` spec references to point to the actual spec files in this change instead of unresolved paths.
- Rechecked task coverage against the issue requirements: backend merge-base semantics, Issue Detail commits, Files changed UX, and regression validation are all represented.
- Revalidated the dependency graph: every non-first task has `dependsOn`, all references point to existing lower-priority tasks, and there are no cycles.

All review criteria pass after fixes.

<promise>PASS</promise>
