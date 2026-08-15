### Requirement: Only a runtime-owned receipt can resolve interrupted Agent work

For an Agent Workflow execution, a Runner SHALL report a terminal result or update interruption only through a durable receipt carrying the complete frozen execution binding and a stable receipt id. The Server SHALL validate the receipt against the current Workflow settlement before applying it. A journal entry whose only record is that the dispatch `started` SHALL remain a recovery fence that never authorizes re-execution.

#### Scenario: Terminal result delivery is interrupted

- **WHEN** the runtime has produced a normalized terminal result and the Runner loses the report transport before acknowledgement
- **THEN** the Runner SHALL retain the same receipt and redeliver it with the original execution identity
- **AND** the Server SHALL apply that result at most once

#### Scenario: Host abort follows a returned result

- **WHEN** the run-lifetime cancellation signal fires after an Action has returned a normalized result
- **THEN** the Runner SHALL durably record and replay that result through the existing result-report acknowledgement path
- **AND** it SHALL NOT execute the Action again after restart

#### Scenario: Process loss leaves only a started fence

- **WHEN** a Runner process is lost after a dispatch is journaled as started but before it has durably written a receipt
- **THEN** no runtime activity, session history, or reconnect SHALL create a task outcome or replacement dispatch
- **AND** the Workflow SHALL retain its existing unresolved recovery state

### Requirement: A receipt carries an immutable identity and exactly one payload

Each receipt SHALL carry an immutable identity — the frozen Workflow execution binding (WorkflowRun, task attempt, work, and Runner identity; AgentSession and AgentTurn identity; runtime and runtime-session identity), the recovery generation, and a stable receipt id — and exactly one payload: either the complete normalized terminal `WorkItemResult` with a fingerprint of that payload, or an `update-interrupted` statement naming the update operation and confirming that the exact bound physical turn is no longer executing. An `update-interrupted` payload SHALL contain no task outcome, and it MUST NOT be manufactured from an idle observation, a missing runtime, or transcript text.

#### Scenario: Receipt is durable before first delivery

- **WHEN** the runtime adapter has produced a terminal result or confirmed the bound turn stopped
- **THEN** the Runner SHALL write the receipt to a Runner-local atomic store before its first delivery attempt

#### Scenario: Interruption payload carries no task outcome

- **WHEN** the Runner writes an `update-interrupted` receipt
- **THEN** the payload SHALL name the update operation and confirm only that the bound physical turn is no longer executing
- **AND** it SHALL NOT encode a task outcome as a failed, cancelled, or unknown result

#### Scenario: Idle observation cannot manufacture a receipt

- **WHEN** the runtime is idle, unreachable, or its transcript contains text but the stop of the bound turn was never runtime-confirmed
- **THEN** the Runner SHALL NOT write an `update-interrupted` receipt for that turn
- **AND** the existing unresolved fence SHALL be preserved with no inference

### Requirement: The Runner retries the exact receipt until the Server acknowledges it

A receipt delivery failure SHALL retain the receipt unchanged, and the Runner SHALL retry delivery of the same receipt id and payload until the Server acknowledges it. The Runner SHALL retire its local receipt only after that durable acknowledgement, and a restart SHALL reload unacknowledged receipts and continue replaying them without re-executing the work.

#### Scenario: Report transport fails before acknowledgement

- **WHEN** a receipt delivery fails or its acknowledgement is lost
- **THEN** the Runner SHALL retry the exact same receipt with the original identity
- **AND** the Server SHALL apply its effect at most once across all deliveries

#### Scenario: Runner restarts with an unacknowledged receipt

- **WHEN** the Runner process restarts while a receipt is durably stored but not yet acknowledged
- **THEN** startup SHALL reload that receipt and resume its delivery
- **AND** the underlying Action SHALL NOT execute again

### Requirement: Server arbitration applies receipts at most once and rejects mismatches

The WorkflowRun SHALL arbitrate receipts in the same persistence transaction as task state. An exact duplicate of an already-applied receipt SHALL be a no-op that returns the same durable acknowledgement. A receipt whose identity or payload mismatches the recorded settlement, or that targets a terminal task, a stopped execution, or a different binding, SHALL be rejected while the original settlement is retained unchanged. The original `AgentResultSettlement` binding SHALL stay frozen: arbitration MUST NOT mutate it.

#### Scenario: Exact duplicate receipt is a no-op

- **WHEN** the same receipt is delivered more than once after it was applied
- **THEN** the Server SHALL return the same durable acknowledgement
- **AND** it SHALL NOT apply the payload, allocate another attempt, or emit another task outcome

#### Scenario: Mismatched receipt is rejected

- **WHEN** a receipt names an execution identity or binding that differs from the recorded settlement for that work
- **THEN** the Server SHALL reject the receipt
- **AND** the original settlement and its frozen binding SHALL remain unchanged

#### Scenario: Terminal-result payload applies through the authoritative settlement

- **WHEN** a `terminal-result` receipt matches the recorded settlement
- **THEN** the Server SHALL apply that result exactly once through the existing authoritative result settlement
- **AND** normal report acknowledgement SHALL retire the Runner-local receipt

### Requirement: A confirmed update interruption creates a distinct replacement attempt

A confirmed update interruption SHALL be a physical-stop fact rather than a task result. Only an `update-interrupted` receipt that matches a durable update-operation fence for the same work MAY cause the Server to create a replacement attempt. On acceptance, the Server SHALL record the original attempt as interrupted history, allocate the next recovery generation, a new AgentTurn, and a dispatch with a new delivery identity, and make exactly one replacement dispatch eligible — committed before the Runner may retire its interruption receipt. The original turn SHALL remain immutable for transcripts, and late events or reports carrying the original AgentTurn identity SHALL be treated as stale and MUST NOT settle or change the replacement attempt.

#### Scenario: Old turn event arrives after a replacement starts

- **WHEN** the Server has accepted a confirmed interruption receipt and created a new AgentTurn for the replacement execution
- **THEN** an event or report carrying the original AgentTurn identity SHALL be stale
- **AND** it SHALL NOT settle or change the replacement attempt

#### Scenario: Interruption receipt is replayed

- **WHEN** the same confirmed interruption receipt is delivered more than once
- **THEN** the Server SHALL return the same durable acknowledgement
- **AND** it SHALL NOT create more than one replacement attempt

#### Scenario: Interruption receipt without a matching fence is rejected

- **WHEN** an `update-interrupted` receipt arrives for work that no durable update operation names as affected
- **THEN** the Server SHALL reject the receipt
- **AND** it SHALL NOT create a replacement attempt or change the existing settlement

### Requirement: Update status remains explicit when a receipt is unavailable

The managed update workflow SHALL require acknowledgement of an exact receipt for every affected active Agent work before it reports that work as recovered. A timeout, an old-process loss, or a missing receipt SHALL leave that work's outcome explicitly unresolved.

#### Scenario: Old Runner is lost during update interruption

- **WHEN** the old Runner exits or becomes unreachable before it has written an exact receipt for an affected work item
- **THEN** the update result SHALL identify that work as unresolved
- **AND** it SHALL NOT claim that the work was recovered or re-dispatch it
