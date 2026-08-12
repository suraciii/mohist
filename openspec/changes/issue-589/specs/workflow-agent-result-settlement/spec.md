### Requirement: Workflow is the authority for Agent task outcomes

For a Workflow-owned Agent task, the WorkflowRun SHALL be the sole authority that records the task as completed or failed. An authoritative Agent result is a result report bound to the recorded Workflow execution identity that explicitly establishes success or failure. Runner connectivity, result-report transport errors, AgentSession activity, AgentTurn activity, and physical stop delivery or confirmation states MUST remain execution observations and MUST NOT by themselves produce `TaskCompleted`, `TaskFailed`, or an equivalent terminal task outcome.

#### Scenario: Stop confirmation fails while the target may still be active

- **WHEN** a physical stop request for a Workflow Agent execution cannot be confirmed and the recorded target may still be active
- **THEN** the Workflow SHALL retain an explicit unknown, nonterminal settlement for the original execution
- **AND** the task and WorkflowRun MUST NOT be recorded as completed or failed

#### Scenario: Physical session becomes idle without a result

- **WHEN** the AgentSession or physical Runtime Session is observed as idle or completed but no authoritative Agent result exists for the bound Workflow execution
- **THEN** the physical activity SHALL be allowed to settle independently
- **AND** the Workflow task outcome SHALL remain unknown and nonterminal

#### Scenario: Physical stop is confirmed without a result

- **WHEN** the stop operation confirms that the recorded physical target stopped but no authoritative Agent result exists
- **THEN** the stop operation SHALL settle as a physical execution fact
- **AND** the Workflow task outcome SHALL remain unknown without recording `TaskFailed` or completion

### Requirement: Unknown settlement preserves one execution identity

An unknown settlement SHALL durably retain the original WorkflowRun, task attempt and work identity, bound AgentSession and AgentTurn identity, applicable physical target, stop-operation identity, unresolved reason, and settlement deadline. The turn-opening input SHALL have a stable delivery identity, and the Agent runtime MUST NOT start until the Server acknowledges that both the AgentSession turn binding and Workflow execution binding are durable. Every later runtime event SHALL retain that immutable execution identity; events from different executions MUST NOT be delivered as one identity. Repeated inputs or observations for the same identity MUST resolve to the same turn and settlement and MUST NOT create replacement work, a replacement Agent turn, a replacement deadline, or a second task outcome.

Task definition identifiers remain scoped to their stage and MAY repeat in different stages. The resulting `TaskRunId` and `WorkId` MUST nevertheless be unique within a WorkflowRun, including the first attempt of two different stages with the same definition identifier. Identity allocation MUST retain the established identifier for a non-colliding attempt and deterministically disambiguate only a colliding later attempt.

#### Scenario: Different stages repeat a task definition identifier

- **WHEN** two stages each execute their first attempt of a task with the same definition identifier
- **THEN** the Workflow SHALL assign distinct TaskRun and work identities within the run
- **AND** a report or observation naming one identity MUST NOT select the other stage's attempt

#### Scenario: Runtime waits for durable Server binding

- **WHEN** the Runner has persisted the turn-opening input locally but has not received the matching Server acknowledgement
- **THEN** the Agent runtime MUST NOT be invoked
- **AND** the Server MUST issue that acknowledgement only after the AgentSession turn binding and Workflow execution binding are both durable
- **AND** the acknowledgement SHALL identify the created or reused AgentTurn for every later runtime event

#### Scenario: Input acknowledgement is lost after durable binding

- **WHEN** the Server durably binds a turn-opening input but its acknowledgement is lost before the Runner receives it
- **THEN** replay of the same input delivery identity SHALL reuse the existing Agent input, AgentTurn, and Workflow settlement
- **AND** the runtime MAY start only after the replay receives the matching acknowledgement

#### Scenario: A runtime-event batch contains different execution identities

- **WHEN** queued runtime events belong to different work attempts, Agent turns, Runners, or runtime Sessions
- **THEN** the delivery path SHALL partition them by full execution identity or reject the mixed batch
- **AND** it MUST NOT serialize all events using metadata taken from only one event in the batch

#### Scenario: The same stop operation is delivered repeatedly

- **WHEN** the same stop operation is delivered again for an execution that already has an unknown settlement
- **THEN** the system SHALL return or continue the existing settlement for that execution identity
- **AND** it MUST NOT create a new stop-operation identity or another Workflow task outcome
- **AND** it MAY redeliver the recorded stop operation only after positive reconciliation proves that the exact recorded target is still active

#### Scenario: Duplicate unknown observations arrive

- **WHEN** AgentSession, Runner, or recovery replay delivers the same unknown execution observation more than once
- **THEN** the Workflow SHALL apply the observation idempotently to the existing settlement
- **AND** the task history MUST contain no duplicate settlement or terminal-result transition

### Requirement: Runner loss preserves unresolved Workflow Agent work

When a Runner disconnects before the Workflow has accepted an authoritative Agent result, the system SHALL preserve the original execution identity, the last unresolved reason, and the existing settlement deadline. Runner loss MUST NOT translate unresolved Workflow Agent work into `TaskFailed`, and reconnect reconciliation MUST continue against the preserved execution rather than dispatching a replacement execution.

#### Scenario: Runner disconnects before result acknowledgement

- **WHEN** a Runner disconnects after the Agent execution starts and before its authoritative result is accepted by the Workflow
- **THEN** the Workflow SHALL retain the same execution identity with an unknown outcome and the disconnect reason
- **AND** it MUST NOT emit `TaskFailed` solely because the Runner disconnected

#### Scenario: Runner reconnects while the outcome is unknown

- **WHEN** the Runner reconnects before the settlement deadline for an unresolved Workflow Agent execution
- **THEN** its observations and any retained result SHALL reconcile the original execution identity
- **AND** the system MUST NOT create or dispatch replacement Workflow work to resolve the uncertainty

### Requirement: Recovery reconciles before repeating a physical effect

Recovery SHALL first reconcile the recorded execution identity with any authoritative Agent result and the current activity of its recorded physical target. Recovery MUST NOT request another physical stop unless that same target is authoritatively observed as still active; any such request SHALL retain the recorded stop-operation identity. An idle, completed, missing, replaced, or indeterminate physical target without an authoritative Agent result MUST leave the Workflow settlement unknown rather than establishing task success or failure.

#### Scenario: Recovery finds the same target still active

- **WHEN** recovery confirms that the exact physical target recorded by the unresolved execution remains active
- **THEN** recovery SHALL continue the recorded stop operation for that target
- **AND** it MUST NOT create a new stop-operation identity or a replacement execution

#### Scenario: Recovery finds the target no longer active

- **WHEN** recovery observes that the recorded physical target is idle, completed, missing, or replaced and still has no authoritative Agent result
- **THEN** recovery MUST NOT request another physical stop
- **AND** the Workflow task outcome SHALL remain unknown until an authoritative result arrives or the settlement deadline expires

#### Scenario: Recovery cannot determine target activity

- **WHEN** recovery cannot authoritatively determine whether the recorded physical target is still active
- **THEN** recovery MUST NOT repeat the physical stop merely to force convergence
- **AND** the existing unknown settlement and deadline SHALL remain unchanged

### Requirement: Unknown settlement reaches a bounded blocked state

The first unknown settlement SHALL have a fixed, durable deadline computed from a configured timeout whose default is five minutes. The deadline MUST NOT be extended by redelivery, reconnect, recovery replay, Server restart, or another observation of the same unresolved execution. If the deadline expires without an authoritative Agent result, the Workflow task SHALL become `blocked`, not completed or failed. Workflow task and run status projections SHALL expose the blocked state and an actionable reason identifying the unconfirmed Agent result and available recovery path; they MUST NOT expose `TaskFailed` as the reason for that settlement.

#### Scenario: Deadline expires without an authoritative result

- **WHEN** the fixed settlement deadline expires while the original execution still has no authoritative Agent result
- **THEN** the Workflow task SHALL be projected as `blocked` with an actionable unresolved-result reason
- **AND** the Workflow MUST NOT record `TaskCompleted`, `TaskFailed`, `StageFailed`, or `WorkflowRunFailed` for the unknown result

#### Scenario: Replay does not move the deadline

- **WHEN** an unresolved execution is redelivered, recovered after restart, or observed again before its deadline
- **THEN** the settlement SHALL retain its original deadline
- **AND** repeated recovery activity MUST NOT keep the execution unresolved beyond the configured bound

#### Scenario: Status consumers read a blocked settlement

- **WHEN** API, event, CLI, or Web Workflow status surfaces read a task whose unknown-result deadline has expired
- **THEN** each surface SHALL represent the task and Workflow attention as blocked rather than failed or completed
- **AND** each surface SHALL expose the persisted actionable reason without requiring inference from AgentSession activity
- **AND** Issue and Inbox SHALL project blocked attention while blocked events MUST NOT be routed to failure-only GitHub, Hermes, or Agent subscribers

### Requirement: Explicit Workflow stop cancels unresolved work without failure

An explicit operator stop SHALL be the only control operation that abandons an unresolved Workflow Agent execution. The stop state commit SHALL atomically cancel the unresolved task, stop the WorkflowRun, and retain the execution identity in history. Dispatch snapshot deletion, settlement reminder removal, and stage-lock release SHALL occur only after that commit through an idempotent cleanup operation. A cleanup interruption MUST be repairable by stop replay or grain activation without changing the committed stop outcome. The stop MUST NOT write task, stage, or run failure fields or events. Any later result or observation for the cancelled execution SHALL be acknowledged as stale without side effects.

#### Scenario: Operator stops a Workflow with an unresolved Agent result

- **WHEN** an operator explicitly stops a Workflow whose Agent result settlement is unknown or blocked
- **THEN** the task SHALL become cancelled and the WorkflowRun SHALL become stopped without a failure outcome
- **AND** the original execution identity SHALL remain available in history
- **AND** the dispatch snapshot, settlement reminder, and stage locks SHALL be released only after the stop state commits
- **AND** any later report or observation for that execution SHALL be stale and side-effect free

#### Scenario: Cleanup is interrupted after the stop commit

- **WHEN** the Workflow stop state commits but snapshot deletion, reminder removal, or stage-lock release fails
- **THEN** the Workflow SHALL remain stopped with its task cancelled and no failure outcome
- **AND** stop replay or grain activation SHALL retry the same cleanup idempotently until the resources are released

### Requirement: An authoritative Agent result settles exactly once

An authoritative Agent result matching the recorded execution identity SHALL settle an unknown or blocked Workflow task exactly once according to the reported success or failure. A successful result SHALL use the normal task-completion and Workflow-advancement semantics; a failed result SHALL use the normal task-failure semantics. After the first authoritative result wins, every later report for that execution identity SHALL receive a stale acknowledgement without events or other side effects; implementations MUST NOT claim duplicate-versus-conflict discrimination unless they persist a result fingerprint. Stale stop observations MUST NOT overwrite the accepted authoritative result.

#### Scenario: Authoritative result arrives before the deadline

- **WHEN** a matching authoritative Agent result arrives while the Workflow settlement is unknown and before its deadline
- **THEN** the Workflow SHALL settle the original task once according to that result
- **AND** it SHALL clear the unresolved settlement without creating replacement work

#### Scenario: Authoritative result arrives after the task is blocked

- **WHEN** a matching authoritative Agent result arrives after the unknown settlement has become blocked
- **THEN** the Workflow SHALL settle the original blocked task once according to that result
- **AND** the earlier blocked observation MUST NOT be treated as a competing task outcome

#### Scenario: Stale stop observation follows an accepted result

- **WHEN** a stale unknown, stopped, idle, or disconnected observation arrives after an authoritative Agent result has settled the task
- **THEN** the Workflow SHALL preserve the accepted task outcome and history
- **AND** the stale observation MUST NOT reopen, block, complete, or fail the task again

#### Scenario: Authoritative result is delivered more than once

- **WHEN** any additional authoritative result is delivered for an execution identity whose task already accepted an authoritative result
- **THEN** each later report SHALL receive a stale acknowledgement
- **AND** the Workflow MUST NOT bind artifacts, mutate output, append another terminal task outcome, or advance the Workflow again

### Requirement: Conclusive failures retain normal Workflow semantics

This settlement behavior SHALL apply only when the result of Workflow-owned Agent execution is not authoritative. A conclusive failed Agent result SHALL still produce the normal Workflow task failure, and unrelated Workflow task failures, dispatch validation failures, workspace preparation timeouts, and check failures MUST NOT be reclassified as unknown or blocked by this capability. The unknown-settlement behavior SHALL apply consistently to Workflow Agent tasks in every stage and profile.

#### Scenario: Agent reports a conclusive failure

- **WHEN** the Workflow accepts an authoritative failed result for the recorded Agent execution
- **THEN** the task SHALL follow the normal `TaskFailed` and Workflow failure behavior
- **AND** it MUST NOT enter unknown settlement merely because a physical session also stopped

#### Scenario: Non-Agent work fails conclusively

- **WHEN** a non-Agent Workflow task, dispatch validation, workspace preparation, or check fails with its existing conclusive failure condition
- **THEN** the existing failure semantics SHALL remain unchanged
- **AND** the failure MUST NOT enter Agent result settlement

#### Scenario: Unknown Agent result occurs in any Workflow stage

- **WHEN** a Workflow-owned Agent execution has an unconfirmed result in plan, build, check, integrate, or a custom stage
- **THEN** the same identity-preserving unknown, reconciliation, deadline, and blocked requirements SHALL apply
