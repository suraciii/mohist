## Self-Review

- Proposal, design, tasks, and spec delta now align on one modified capability: `web-ui`.
- The proposal covers all issue requirements called out in the acceptance criteria: desktop horizontal layout, decision-first attention summary, compact mobile controls, full label reachability, done de-emphasis, and regression coverage.
- The design stays consistent with the proposal and current codebase constraints by keeping the change frontend-only and deriving attention state from existing issue and agent fields.
- The change-local spec delta now exists at `specs/web-ui/spec.md` and defines four concrete requirement anchors used by `tasks.json`.
- Every task has a verifiable outcome, every non-first task has `dependsOn`, all dependency references point to existing lower-priority tasks, and the graph is acyclic.
- Task granularity is appropriate for autonomous execution: layout, attention summary, compact full-label filters, and regression coverage are independently deliverable and build on each other in order.

<promise>PASS</promise>
