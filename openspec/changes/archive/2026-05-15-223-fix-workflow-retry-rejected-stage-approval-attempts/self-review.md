## Self-Review

### Alignment

- Proposal addresses the issue's core failure: rejected approval feedback is recorded, but `resume-pipeline` skips because the issue projection is `blocked`.
- The change remains generic to stage retryability and keeps `plan` as `Stage.name = "plan"`.
- The proposal, design, specs, and tasks all preserve the non-goal of not making arbitrary blocked issues runnable.

### Completeness

- Added delta specs for all modified capabilities declared in the proposal: `workflow-run`, `pipeline-model`, and `coder-session-tracking`.
- Specs cover retryability evaluation, blocked resume behavior, rejection feedback persistence, new session observability, approval re-request, and negative blocked behavior.
- Tasks cover each spec area and include final regression coverage.

### Consistency

- Task spec references now point to spec files that exist in this change directory.
- Design decisions align with the delta specs and tasks: aggregate retryability, queue-worker blocked handling, rejection feedback preservation, and existing session observability.
- Naming is consistent with existing concepts: `WorkflowRun`, `resume-pipeline`, `Stage.name`, approval rejection, and `coder_session` observability.

### Feasibility

- Tasks are implementable in autonomous iterations and ordered by value: retryability predicate, queue behavior, feedback persistence, Plan retry context, regression tests.
- No database migration or new first-class stage-attempt storage is required.
- The implementation relies on existing workflow aggregate and queue paths.

### Dependency Completeness

- Every non-first task has `dependsOn`.
- All dependencies reference existing earlier task IDs with lower priority.
- The dependency graph is acyclic.

<promise>PASS</promise>
