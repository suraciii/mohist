## Self Review

### Alignment

- Proposal addresses the issue's core requirement: introduce a first-class WorkflowRun runtime root for issue advancement.
- What Changes entries trace to issue requirements: start creates run, StageRuns/tasks/checks are materialized, Build tasks come from `tasks.json`, runtime-added work remains normal tasks with explanation metadata, UI/API read WorkflowRun, evidence/logs/checkpoints keep separate roles, and DSL/policy/session-internals remain out of scope.
- No issue requirement is intentionally omitted from proposal, design, specs, or tasks.

### Completeness

- Added missing delta specs for all proposal capabilities: `workflow-run`, `workflow-engine`, `pipeline-model`, `http-api`, and `web-ui`.
- Specs cover start behavior, seeded Plan StageRun data, Build materialization, runtime-added task metadata, evidence/checkpoint separation, engine updates, API exposure, compatibility projection, and UI rendering semantics.
- Tasks cover each spec area and include regression coverage.

### Consistency

- Proposal capabilities align with files under `specs/`.
- Task spec references resolve to existing spec files and requirement ids.
- Design decisions align with the added specs and tasks.
- Naming is consistent: WorkflowRun, StageRun, Task, Check, evidence/audit, and resume cursor are kept distinct.

### Feasibility

- Tasks are ordered by deliverable capability: persistence/service, start/API, runner updates, Build materialization, dynamic work, compatibility projection, UI, and tests.
- Each task is scoped to one outcome and has verifiable acceptance criteria.
- Existing structures are preserved for compatibility, reducing migration risk.

### Dependency Review

- `tasks.json` parses as valid JSON.
- Every non-first task has `dependsOn`.
- All dependencies reference existing lower-priority task ids.
- The dependency graph is acyclic.
- All non-empty task spec references resolve to existing files and requirement ids.

<promise>PASS</promise>
