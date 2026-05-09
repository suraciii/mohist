## Self Review

Reviewed `proposal.md`, `design.md`, `tasks.json`, and the change `specs/` directory against alignment, completeness, consistency, feasibility, and dependency criteria.

## Findings And Fixes

- Found missing delta spec for the modified `worktree-manager` capability declared in the proposal. Added `specs/worktree-manager/spec.md` with requirements for forced squash merge, issue-level squash commit messages, and removal of fast-forward status from successful merge results.
- Updated `tasks.json` so every task references the new `worktree-manager` spec requirement it implements or verifies.
- Resolved design open questions directly in `design.md`: no replacement `strategy` field is added, and `rebased` may remain only as optional pre-squash diagnostic metadata.

## Validation

- Proposal now aligns with issue scope and modified capability.
- Specs cover the forced squash merge behavior, commit message generation, failure handling, and `fastForward` contract removal.
- Tasks cover all spec requirements and have a valid linear dependency graph: `T-001 -> T-002 -> T-003 -> T-004`.
- Every non-first task has `dependsOn`, all dependencies point to lower priorities, and there are no cycles.
- `tasks.json` parses as valid JSON.

<promise>PASS</promise>
