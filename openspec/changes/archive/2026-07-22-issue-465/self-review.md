# Self Review

## Scope Reviewed

- `proposal.md`
- `design.md`
- `specs/task-input-rendering-boundary/spec.md`
- `specs/recovery-self-retry-declaration/spec.md`
- `tasks.json`

## Findings

No blocking findings.

The proposal identifies the actual value-leak boundary: Server expansion of a
task declaration followed by recovery copying that expanded dispatch value.
The specifications cover raw declaration dispatch, immutable attempt
snapshots, Runner-only rendering and validation, deferred inputs, and the
model-a to model-b self-retry regression. The design uses one raw dispatch
representation and one short-lived rendered Action input, explicitly rejecting
the prohibited `rawWith`/`rawTask` escape hatch.

The implementation graph is appropriately ordered. T-001 establishes raw
dispatch, Runner rendering, cloning, type renaming, documentation, and its
own coverage. T-002 consumes that representation to protect recovery
continuations and covers the issue's model-switch, failure-reference, and
budget invariants. Its dependency points to an existing lower-priority task,
so the graph is acyclic.

The plan also preserves the stated non-goals: it keeps Effective Variable
resolution at dispatch time, does not migrate baked historical TaskRuns, does
not change the public Workflow DSL, and leaves recovery matching, ordering,
and budget ownership in the Runner.

<promise>PASS</promise>
