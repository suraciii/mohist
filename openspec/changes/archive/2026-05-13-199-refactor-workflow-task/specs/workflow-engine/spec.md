## MODIFIED Requirements

### Requirement: Stage execution infrastructure exposes shared stage side-effect helpers

Stage execution infrastructure SHALL expose shared stage-scoped safe `emit` and `log` helpers through `StageContext` or an equivalent shared stage runtime boundary.

#### Scenario: Runners reuse shared safe side-effect helpers

- **WHEN** Plan, Build, Check, or Integrate code needs to emit an existing workflow event or write a workflow log entry
- **THEN** it uses the shared stage-scoped helper instead of maintaining a runner-private `emitSafe` or `writeLog` implementation
- **AND** emitted event names and payload shapes remain unchanged
- **AND** workflow log event types and payload semantics remain unchanged

#### Scenario: Side-effect helper failures stay non-fatal

- **WHEN** the underlying event bus emit or workflow log insert throws or rejects
- **THEN** the shared helper swallows the infrastructure failure
- **AND** stage execution continues through the existing runner control flow

### Requirement: Non-Build tasks execute through a minimal shared handler contract

Non-Build task execution SHALL support a minimal shared runtime contract where a task definition plus `StageContext` resolves to an executable task and executes through a handler that returns normalized `StageTaskResult`-style output.

#### Scenario: Shared handler execution preserves runner ownership

- **WHEN** a Plan, Check, or Integrate task executes through the shared runtime
- **THEN** the handler executes only that task and returns normalized task status, output, and timing data
- **AND** the handler does not write checkpoints, transition stages, request approval, or decide workflow progression
- **AND** the runner remains responsible for retries, reporting, checks, and final stage success or failure

### Requirement: Static task loading is available for Plan Check and Integrate tasks

The workflow runtime SHALL support a static task loader that prepares executable Plan, Check, and Integrate tasks from `StageContext` without taking over Build or Ralph execution behavior.

#### Scenario: Static definitions resolve executable task input

- **WHEN** a static Plan, Check, or Integrate task definition is loaded
- **THEN** the loader resolves prompt or service-call input from `StageContext`
- **AND** it returns executable tasks in the same order as the supplied static definitions
- **AND** it does not introduce Build dynamic ordering, `dependsOn`, checkpoint logic, or Ralph task execution behavior

### Requirement: Legacy repair and fix entrypoints remain compatible through shared adapters

Legacy repair and fix task entrypoints SHALL remain available while dispatching through shared adapter-backed task execution.

#### Scenario: Shared adapter covers current repair and fix task ids

- **WHEN** the workflow executes an existing plan repair, review repair, merge repair, or stage health fix path
- **THEN** the legacy entrypoint resolves the real current task id through a shared adapter or registry-backed path
- **AND** the task executes through the shared handler contract appropriate for that task type
- **AND** compatibility exports such as `runHealthFixTask`, `runReviewFixTask`, and `runPlanRepairTask` remain available or have preserved equivalent entrypoints

### Requirement: Agent-session tasks share a reusable execution primitive

Agent-session-backed workflow tasks SHALL execute through a reusable `AgentSessionTaskHandler` execution primitive.

#### Scenario: Agent-session task normalizes execution outcomes

- **WHEN** a Plan or Check task, or an agent-backed repair task, executes through `AgentSessionTaskHandler`
- **THEN** the handler can report success, task failure, or retry-after-missing-artifact style results through normalized task output
- **AND** existing task-level events such as `stage_task_update` may still be emitted through the shared stage helper
- **AND** artifact verification or retry prompting remains scoped to the task execution boundary rather than stage progression

### Requirement: Service-backed workflow steps share a reusable execution primitive

Service-backed workflow tasks SHALL execute through a reusable `ServiceCallTaskHandler` execution primitive.

#### Scenario: Service-call task normalizes integrate and merge-style work

- **WHEN** an Integrate step or merge-style repair task invokes repository or application services through `ServiceCallTaskHandler`
- **THEN** the handler normalizes successful and failed service invocation results into `StageTaskResult`-style output
- **AND** the task continues to rely on the runner for stage-level events, checks, and final workflow decisions
