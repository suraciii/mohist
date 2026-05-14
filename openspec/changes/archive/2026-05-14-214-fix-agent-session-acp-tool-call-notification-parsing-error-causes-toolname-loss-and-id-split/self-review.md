## Self Review

### Alignment

- Proposal addresses the issue: ACP tool call notifications can lose `toolName` and split ids when identity fields appear outside the currently parsed nested shape.
- The change scope stays focused on AgentSession normalization, observer payload quality, persistence/replay identity, and regression coverage.

### Completeness

- Added delta specs for all modified capabilities listed in the proposal: `agent-runtime`, `coder-session-tracking`, and `pipeline-session-events`.
- Specs cover top-level/nested name recovery, provider id preference, synthetic id reuse, `tool_call_update` normalization, live coder event identity, replay convergence, and plan/check raw bridge payloads.
- Tasks cover every delta spec.

### Consistency

- Design aligns with the specs by placing normalization at the AgentSession boundary before observer dispatch.
- Tasks now reference real delta spec requirement ids: `REQ-AR-214`, `REQ-CST-214`, and `REQ-PSE-214`.
- Naming is consistent across proposal, design, specs, and tasks.

### Feasibility

- Task granularity is appropriate: one implementation task followed by targeted regression/bridge verification tasks.
- The implementation requires no migration, config change, API schema change, or new dependency.

### Dependency Completeness

- T-001 has no dependency and produces normalized AgentSession behavior.
- T-002 depends on T-001 because its regression tests consume normalized runtime behavior.
- T-003 depends on T-001 because bridge verification consumes normalized raw observer payloads.
- Dependency graph is acyclic and all dependencies point to lower-priority existing tasks.

<promise>PASS</promise>
