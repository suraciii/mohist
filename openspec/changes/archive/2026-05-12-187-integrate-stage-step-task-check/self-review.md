## Self-Review

Reviewed `proposal.md`, `design.md`, and `tasks.json` against the issue scope, acceptance criteria, and artifact consistency rules.

Issues found and fixed during review:

- The change directory had no delta specs even though the proposal declared modified capabilities for `workflow-run`, `workflow-definition`, `pipeline-model`, and `web-ui`.
- `tasks.json` referenced spec paths and requirement IDs that did not exist inside this change directory.

Fixes applied:

- Added delta specs for:
  - `specs/workflow-run/spec.md`
  - `specs/workflow-definition/spec.md`
  - `specs/pipeline-model/spec.md`
  - `specs/web-ui/spec.md`
- Updated `tasks.json` to reference the new local requirement IDs:
  - `REQ-WR-005`
  - `REQ-WD-002`
  - `REQ-PM-007`
  - `REQ-WUI-005`

Post-fix review result:

- Proposal scope matches the issue: Integrate task/check standardization, visibility, and shared failure semantics.
- Design matches the proposal and stays within current framework limits, especially keeping merge/spec-sync/archive as tasks and limiting `CheckFailurePolicy` use to `health:integrate`.
- Specs now cover each modified capability named in the proposal.
- Every task references an existing local spec requirement.
- Every non-first task has `dependsOn`, all dependencies point to existing earlier tasks, and the graph is acyclic.
- Task granularity is appropriate for autonomous execution and follows a valid implementation order.

<promise>PASS</promise>
