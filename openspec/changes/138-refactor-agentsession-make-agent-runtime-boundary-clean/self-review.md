## Self-Review

### Alignment

- Proposal addresses the issue requirement to clean the `AgentSession` domain boundary by removing workflow visibility dependencies from runtime options and moving persistence/EventBus behavior behind observers.
- Design follows the requested two-phase shape: boundary cleanup first, optional ACP driver extraction only if it reduces responsibility.
- Tasks trace to the required implementation outcomes: observer boundary, runtime cleanup, consumer migration, regression tests, optional ACP driver, and final verification.

### Completeness

- Added missing delta specs for all proposal modified capabilities: `agent-runtime`, `workflow-log`, `pipeline-session-events`, and `coder-session-tracking`.
- Specs cover the key acceptance criteria: no EventBus/DB repo types in `AgentSessionOptions`, no runtime imports of workflow visibility layers, explicit observers, Plan/Check multi-round reuse, lifecycle cleanup, model override behavior, visibility persistence, realtime events, and optional ACP driver constraints.
- Tasks cover all specs and include verification for high-risk regressions: Plan/Check session reuse, abort/timeout cleanup, coder session status, SSE events, model override, and full build/test verification.

### Consistency

- Proposal capabilities match change-local delta specs under `specs/<capability>/spec.md`.
- Task `spec` paths resolve to existing change-local spec files.
- Design naming is consistent with the issue language: `AgentSession`, `SessionObserver`, `WorkflowSessionObserver`, workflow/service observer helper, and optional `AcpConnectionDriver`.
- No frontend API, SSE event name, or schema change is proposed.

### Feasibility

- Tasks are ordered by outcome and each task is small enough for one implementation iteration.
- The dependency graph is valid: every non-first task has dependencies, every dependency references an earlier task, and validation found no missing spec paths.
- Optional ACP driver extraction is explicitly guarded so implementation can skip it if it would be a shallow wrapper.

### Fixes Applied During Review

- Created missing delta spec files for all modified capabilities because the change-local `specs/` directory was initially empty.
- Validated `tasks.json` syntax, dependency ordering, and spec path resolution after adding specs.

<promise>PASS</promise>
