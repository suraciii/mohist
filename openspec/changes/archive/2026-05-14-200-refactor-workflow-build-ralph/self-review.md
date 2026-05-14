## Self-Review

- Reviewed `proposal.md`, `design.md`, and `tasks.json` against the issue scope, acceptance criteria, and dependency rules.
- Found and fixed one consistency issue during review: the change declared a modified `ralph-task-execution` capability but did not yet include a change-local delta spec. Added `specs/ralph-task-execution/spec.md` and kept `tasks.json` aligned to that spec.
- Re-checked task dependencies after the fix:
  - every non-first task has at least one `dependsOn`
  - all dependencies reference existing lower-priority task IDs
  - the graph is acyclic
- Re-checked artifact alignment after the fix:
  - proposal capability change now has a matching change-local spec
  - design decisions match the proposal scope and non-goals
  - tasks cover loader, handler, compatibility wrapper, and regression testing outcomes

<promise>PASS</promise>
