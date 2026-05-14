Reviewed `proposal.md`, `design.md`, `tasks.json`, and added the missing spec deltas under `specs/` so the artifact set now covers capability changes in `workflow-run`, `workflow-engine`, `http-api`, and `web-ui`.

Checks performed:

- Proposal scope and impact align with the issue's product behavior and invariants.
- Design matches the proposal and keeps rebase scheduling, execution, and SHA-based invalidation on the same boundaries described in the codebase.
- Tasks now reference real spec files and real requirements.
- Every non-first task has valid `dependsOn` entries pointing only to earlier tasks.
- The dependency graph is acyclic and appropriately linear for this change.

Residual note:

- The Build-stage invalidation policy remains intentionally narrow in design/tasks because the issue text explicitly excludes replan/re-review side effects and does not define a broader Build reset contract.

<promise>PASS</promise>
