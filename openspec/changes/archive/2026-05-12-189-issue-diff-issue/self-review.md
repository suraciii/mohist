Self-review completed against proposal, design, tasks, and local spec deltas.

Checks performed:

- Alignment: proposal now stays within the implemented scope and no longer implies a new behind-base UI warning in this change.
- Completeness: added local `specs/http-api/spec.md` and `specs/issue-review-surface/spec.md` deltas so the modified capabilities declared in the proposal are actually specified in this change.
- Consistency: tasks now reference spec files that exist inside this change directory, and the design remains aligned with those specs.
- Feasibility: task ordering remains linear and practical for autonomous execution.
- Dependency completeness: every non-first task depends on earlier existing task IDs only, and the graph is acyclic.

No further artifact changes are required.

<promise>PASS</promise>
