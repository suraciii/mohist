Reviewed `proposal.md`, `design.md`, `tasks.json`, and the change-local `specs/` directory for alignment, completeness, consistency, feasibility, and dependency correctness.

Fixes made during self-review:

- Added change-local spec files for `issue-changed-files-reader`, `issue-review-surface`, and `web-ui` so the change now has explicit requirements covering the new reading page, the modified review surface, and the Issue Detail entry/navigation behavior.
- Updated `tasks.json` so task `spec` references point at real change-local spec requirements and every declared capability/spec family is covered by at least one implementation task.

Final checks:

- Proposal capabilities now match the change-local specs directory.
- Design decisions align with the proposal and the new specs.
- Task dependencies form a valid DAG and every non-first task has `dependsOn` entries pointing only to earlier tasks.
- No artifacts were deleted.

<promise>PASS</promise>
