## ADDED Requirements

### Requirement: Single-runner claim invariant

A WorkflowRun SHALL be claimed by at most one runner for its entire lifecycle. Once a `Claim` exists, its `RunnerId` SHALL be the unique runner identity for that run. Any `TaskRun` in `Running` state and any dispatched `StageCheck` SHALL carry a runner identity equal to the `Claim.RunnerId` as a *derivation* of this single-runner invariant, not as a separately-synchronized fact. This derivation is the sole basis on which a workflow run MAY relate a runner identity to its sessions or work items.

#### Scenario: A second runner claim is rejected

- **WHEN** a WorkflowRun already has a `Claim` with `RunnerId = R1`
- **AND** a different runner `R2` attempts to claim the same run
- **THEN** the second claim SHALL be rejected
- **AND** the existing `Claim.RunnerId` SHALL remain `R1`

#### Scenario: A running task runner identity derives from the claim

- **WHEN** a `TaskRun` is in `Running` state
- **THEN** the `TaskRun.RunnerId` SHALL equal the WorkflowRun `Claim.RunnerId`
- **AND** this equality SHALL hold as a consequence of the single-runner invariant rather than as an independently-kept-in-sync fact

#### Scenario: An in-flight check runner identity derives from the claim

- **WHEN** a `StageCheck` is dispatched and in-flight
- **THEN** the `StageCheck.DispatchRunnerId` SHALL equal the WorkflowRun `Claim.RunnerId`
- **AND** result matching SHALL verify the reporting runner against the `Claim.RunnerId`

### Requirement: WorkflowRun status and TaskRun status are independent state machines

`WorkflowRun.Status` and `TaskRun.Status` SHALL be independent state machines that describe their own aggregates and SHALL NOT derive each other. A `WorkflowRun` status transition SHALL NOT be computed as a function of task statuses, and a `TaskRun` status transition SHALL NOT be computed as a function of the `WorkflowRun` status. The two facts MAY coexist or diverge temporarily (for example, a `WorkflowRun` MAY be `Running` while no `TaskRun` is `Running`, such as before the first task has started). Workflow-level command results (`Paused`, `Stopped`, `AwaitingApproval`) SHALL only originate from workflow-level commands and SHALL NOT be derivable from any `TaskRun` status.

#### Scenario: A running workflow does not require a running task

- **WHEN** a `WorkflowRun` has status `Running`
- **AND** no `TaskRun` is in `Running` state
- **THEN** the `WorkflowRun.Status` SHALL remain `Running`
- **AND** the `WorkflowRun.Status` SHALL NOT be recomputed from the absence of `Running` tasks

#### Scenario: A non-terminal task status transition does not recompute the workflow status

- **WHEN** a `TaskRun` transitions between `Pending`, `Running`, or `Completed`
- **THEN** the `WorkflowRun.Status` SHALL NOT be recomputed as a function of that task transition
- **AND** the transition SHALL only mutate the `TaskRun` aggregate's own state

#### Scenario: Task failure is a workflow policy reaction, not a status derivation

- **WHEN** a `TaskRun` transitions to `Failed` and the `WorkflowRun` correspondingly transitions to `Failed`
- **THEN** the workflow transition SHALL be an event-driven policy reaction of the workflow aggregate to the task result
- **AND** the `WorkflowRun.Status` SHALL NOT be a continuous function of task statuses (no status-sync-from-tasks path SHALL exist)

#### Scenario: Workflow command statuses are workflow-level only

- **WHEN** a `WorkflowRun` receives a workflow-level command (`pause`, `stop`, or an approval gate)
- **THEN** the resulting status (`Paused`, `Stopped`, or `AwaitingApproval`) SHALL reflect that workflow command
- **AND** the status SHALL NOT be derivable from any `TaskRun` status

### Requirement: AgentSession is a peer aggregate associated by task reference

`AgentSession` SHALL be a peer-level aggregate root associated with a `WorkflowRun` only through `TaskRun` session references, with no parent-child ownership by the `WorkflowRun`. The workflow-to-session relationship SHALL be expressed in code (method names, field names, comments, and documentation) as *association by reference*, never as ownership. No method name, field name, or comment within the workflow aggregate or its direct consumers SHALL imply that a `WorkflowRun` owns an `AgentSession`. The single-runner claim invariant (above) is the real basis for relating a runner identity to a session; a workflow run does not own sessions by virtue of being a run.

#### Scenario: Session association is judged by claim runner identity, not ownership

- **WHEN** the system determines whether an `AgentSession` is associated with a `WorkflowRun`
- **THEN** it SHALL judge association through the `TaskRun` session reference and the `Claim.RunnerId` identity
- **AND** it SHALL NOT model the relationship as the `WorkflowRun` owning the `AgentSession`

#### Scenario: No code expression implies session ownership

- **WHEN** the workflow aggregate and its direct consumers (querier, view, projection, session-association logic) reference an `AgentSession`
- **THEN** no method name, field name, or comment SHALL imply that a `WorkflowRun` owns an `AgentSession`
- **AND** the relationship SHALL be documented as peer-level association by reference

### Requirement: Cached runner identity has an explicitly declared role

A workflow-runtime value that retains a runner identity after a `Claim` is released or absent (used for recovery or reconciliation) SHALL have its role explicitly declared in code, via naming or comment, as either a derived attribute of the claim model or pure grain-infrastructure recovery state. Such a value SHALL be distinguishable from an active `Claim.RunnerId` by a reader. While no `Claim` exists, `WorkflowRun.IsClaimed` SHALL remain `false`, and a retained recovery identity SHALL NOT constitute or be reported as an active claim.

#### Scenario: A released claim retains a labeled recovery identity

- **WHEN** a `WorkflowRun`'s `Claim` has been released or is absent
- **AND** a runtime value retains the most recent runner identity for recovery or reconciliation
- **THEN** that value's role SHALL be explicitly declared in code (via naming or comment) as a recovery or derived value
- **AND** the value SHALL be distinguishable from an active `Claim.RunnerId` by a reader

#### Scenario: A recovery identity is not an active claim

- **WHEN** a retained runner identity is consulted while no `Claim` exists
- **THEN** `WorkflowRun.IsClaimed` SHALL remain `false`
- **AND** the retained identity SHALL NOT be reported as an active claim to consumers that distinguish active claims from historical identity
