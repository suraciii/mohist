## Review Summary

Reviewed `proposal.md`, `design.md`, and `tasks.json` against alignment, completeness, consistency, feasibility, and dependency rules.

## Findings

- Missing capability specs were the primary gap: the proposal declared modified capabilities, but the change directory had no `specs/*/spec.md` files, so tasks could not reference concrete requirements.
- `design.md` contained an outdated statement saying the change had no checked-in specs.
- `tasks.json` left every `spec` field empty, which weakened traceability from proposal capabilities to executable tasks.

## Fixes Applied

- Added capability specs for `agent-session-ui`, `session-timeline-ui`, `http-api`, `coder-session-tracking`, and `pipeline-session-events`.
- Updated `design.md` so it no longer states that specs are missing.
- Updated every task in `tasks.json` to reference a concrete requirement path.

## Result

The artifacts are now aligned:

- Proposal capabilities are represented by checked-in specs.
- Design decisions are consistent with the capability requirements.
- Tasks reference real requirements and maintain a valid dependency DAG.
- Every non-first task has `dependsOn`, and all dependencies point to existing lower-priority tasks.

<promise>PASS</promise>
