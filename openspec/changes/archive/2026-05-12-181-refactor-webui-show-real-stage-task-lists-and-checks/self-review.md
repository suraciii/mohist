## Self-Review

- Added missing local delta specs for `pipeline-model`, `http-api`, and `web-ui` so the change artifacts now match the proposal's declared modified capabilities.
- Corrected `tasks.json` spec references to point at requirement IDs that now exist inside this change's `specs/` directory.
- Re-checked the task dependency graph: `T-001` has no dependencies, every later task depends on lower-priority tasks only, and there are no cycles.
- Re-checked artifact alignment: proposal, design, specs, and tasks now consistently describe the same backend projection fix, API contract change, UI unification, and regression coverage.

<promise>PASS</promise>
