Reviewed `proposal.md`, `design.md`, and `tasks.json` against alignment, completeness, consistency, feasibility, and dependency completeness.

Fixes made during self-review:

- Added change-local delta specs under `specs/agent-session-ui/spec.md` and `specs/pipeline-session-events/spec.md` so the change directory now has a complete proposal/specs/design/tasks artifact set.
- Updated `tasks.json` so each task references a spec in this change directory instead of pointing at global capability specs.
- Verified the task dependency graph remains a DAG, every non-first task has `dependsOn`, and all dependencies point to existing earlier tasks.

Residual observations:

- `proposal.md` intentionally modifies existing capabilities only; no new capability was needed for this change.
- `design.md` remains consistent with the now-added delta specs: frontend-first ToolRegistry, projection-layer reasoning compensation, live/replay convergence, and optional later timestamp precision upgrade.
- `tasks.json` now covers all change-local spec requirements and keeps the implementation split into independently deliverable units.

<promise>PASS</promise>
