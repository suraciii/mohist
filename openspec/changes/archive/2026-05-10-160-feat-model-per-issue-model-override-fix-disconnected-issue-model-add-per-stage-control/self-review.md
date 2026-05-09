## Self-Review

Reviewed `proposal.md`, `design.md`, `tasks.json`, and the change specs for alignment, completeness, consistency, feasibility, and dependency correctness.

## Findings And Fixes

- Added missing delta specs for all modified capabilities listed in the proposal: `local-issue-store`, `http-api`, `workflow-engine`, `agent-runtime`, and `web-ui`.
- Updated `tasks.json` spec references to point at concrete change spec requirements instead of only base capability spec files.
- Revalidated `tasks.json` JSON parsing and dependency graph: all non-first tasks have dependencies, all dependencies reference existing lower-priority tasks, and no cycles or forward dependencies were found.

## Result

The artifacts now cover the issue requirements: disconnected `issue.model`, per-issue per-stage overrides, corrected executable stage lists, discovery without ACP session pollution, API/storage/UI wiring, recovery-session model resolution, tests, and final verification.

<promise>PASS</promise>
