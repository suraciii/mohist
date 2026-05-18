## Self Review

Outcome: PASS

Issues found and fixed:

- Added missing change-local delta specs for all modified capabilities declared in `proposal.md`: `workflow-definition`, `workflow-run`, `workflow-agent`, `coder-session-tracking`, and `session-timeline-ui`.
- Updated every task in `tasks.json` to reference the relevant spec requirement anchors instead of leaving `spec` empty.

Post-fix checks:

- Proposal, design, specs, and tasks now cover the issue requirements for optional `agentSessionRef`, task-local default behavior, shared default Plan session, multiple refs in one stage, restore/skip stability, fresh sessions on retry/rerun/rewind, separate task progress, coherent prompt blocks, and regression coverage.
- Specs align with the proposal's modified capabilities.
- Tasks cover the specs and have feasible sequencing.
- Dependency validation passed: every non-first task has `dependsOn`, all dependencies point to existing lower-priority task IDs, and no cycles were found in the linear chain.
- `tasks.json` parses successfully and `git diff --check` reports no whitespace errors for the change artifacts.

<promise>PASS</promise>
