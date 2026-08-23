### Requirement: A task's own cleanup turn converges on delivered terminal facts

For a work item's own worktree-cleanup follow-up turn (a positive cleanup attempt on the same work item), the Runner MUST wait — event-driven, within a bounded budget — for the immediately preceding turn's terminal close/idle facts to complete outbound delivery from the runtime-event outbox before opening the Workflow AgentSession and submitting the cleanup turn. Cleanup attempt 1 MUST wait for the original Workflow turn's retained records. Cleanup attempt N greater than 1 MUST wait for attempt N minus 1's cleanup boundary and Session-scoped `session-followup` runtime input and terminal facts, correlated by the prior attempt's deterministic cleanup operation id. This MUST hold for both the OpenCode path and the Pi path. Once the wait completes, cleanup admission MUST proceed even though the session-activity projection observed before the wait was `active` or `unknown`, and the cleanup turn MUST NOT be rejected by the unsettled-session fail-closed guard on account of that pre-wait projection.

#### Scenario: OpenCode cleanup turn under delivery lag

- **WHEN** an OpenCode task turn has completed with its artifacts recorded and the worktree still dirty, its terminal close/idle facts are durable in the runtime-event outbox but have not completed outbound delivery, and the session-activity projection at the cleanup turn's start is `active` or `unknown`
- **THEN** the Runner MUST wait for those terminal facts to complete outbound delivery before opening the Workflow AgentSession for the cleanup turn
- **AND** admission MUST proceed and the cleanup prompt MUST be submitted to the same runtime session
- **AND** the cleanup turn MUST NOT fail with the unsettled-session `session-binding-failed` rejection that states the previous Runtime Session has not reached a terminal state

#### Scenario: Pi cleanup turn under delivery lag

- **WHEN** a Pi task turn has completed with its artifacts recorded and the worktree still dirty, its terminal idle/turn facts are durable in the runtime-event outbox but have not completed outbound delivery, and the session-activity projection at the cleanup turn's start is `active` or `unknown`
- **THEN** the Runner MUST wait for those terminal facts to complete outbound delivery before opening the Workflow AgentSession and submitting the cleanup turn
- **AND** the cleanup channel admission against the frozen execution binding MUST succeed, because the original turn is terminal server-side once the facts are delivered
- **AND** the cleanup turn MUST NOT fail with a frozen-binding conflict caused by the original turn not yet being terminal server-side

#### Scenario: Later OpenCode cleanup attempt under prior cleanup delivery lag

- **WHEN** an OpenCode cleanup attempt completes but leaves the worktree dirty, its Session-scoped `session-followup` terminal activity remains retained, and the next bounded cleanup attempt starts
- **THEN** the Runner MUST wait for the immediately preceding cleanup attempt's boundary and correlated `session-followup` records before opening the Workflow AgentSession
- **AND** the next cleanup prompt MUST be admitted rather than rejected because the prior cleanup turn is still projected active server-side

#### Scenario: Later Pi cleanup attempt under prior cleanup delivery lag

- **WHEN** a Pi cleanup attempt completes but leaves the worktree dirty, its Session-scoped `session-followup` terminal activity remains retained, and the next bounded cleanup attempt starts
- **THEN** the Runner MUST wait for the immediately preceding cleanup attempt's boundary and correlated `session-followup` records before opening the Workflow AgentSession
- **AND** the next `session.cleanup` boundary MUST be enqueued only after the prior cleanup turn is terminal server-side

#### Scenario: Terminal facts already delivered

- **WHEN** the immediately preceding turn's terminal facts have already completed outbound delivery at the moment the cleanup turn starts
- **THEN** the wait MUST complete without added delay
- **AND** cleanup admission MUST proceed without re-consulting the session-activity projection as a gate

### Requirement: Budget-exhausted delivery waits fail with structured evidence

When the delivery wait for the previous turn's terminal facts exceeds its budget, the cleanup attempt MUST fail with structured evidence identifying the awaited Workflow session, the work item, and the exhausted budget. The failure MUST NOT surface as a generic unsettled-session error.

#### Scenario: Delivery lag outlives the wait budget

- **WHEN** the previous turn's terminal facts remain undelivered past the wait budget
- **THEN** the cleanup attempt MUST fail
- **AND** the failure evidence MUST identify the awaited Workflow session, the work item, and the budget that was exhausted
- **AND** the failure MUST NOT be reported as the generic unsettled-session rejection that states the previous Runtime Session has not reached a terminal state

### Requirement: New task attempts reusing a genuinely pending session stay fail-closed

The fail-closed guard for turn admission other than a work item's own cleanup follow-up MUST keep its current behavior. A new task attempt that opens a Workflow AgentSession whose previous runtime session is projected `active` or `unknown` MUST fail closed with the existing `session-binding-failed` unsettled-session error. Only same-work-item cleanup admission stops consulting the lagging session-activity projection.

#### Scenario: Cross-attempt reuse of a genuinely pending session

- **WHEN** a task attempt that is not a cleanup follow-up of the same work item opens a Workflow AgentSession whose previous runtime session has not reached a terminal state in the server projection
- **THEN** admission MUST fail closed with `session-binding-failed`
- **AND** the failure message MUST state that the previous Runtime Session has not reached a terminal state and that retry is fail-closed

### Requirement: Cleanup semantics, budgets, and server-side validation are unchanged

The admission wait MUST NOT alter the cleanup prompt semantics, the cleanup attempt budget, or the server-side cleanup admission contract. The server's cleanup-turn route and its frozen execution binding validation MUST keep their current behavior, and task success or failure SHALL be decided by the actual cleanup result.

#### Scenario: Admission wait does not change cleanup execution

- **WHEN** a cleanup turn is admitted after the delivery wait completes
- **THEN** the cleanup turn MUST run with the same constrained cleanup prompt and the same maximum cleanup attempt budget as before this change
- **AND** each allowed attempt, including attempt 2 and later, MUST remain usable when only the preceding cleanup turn's terminal-fact delivery is delayed
- **AND** task completion MUST still require a clean worktree after the bounded cleanup attempts

#### Scenario: Server-side validation is not weakened

- **WHEN** a cleanup submission reaches the server while its original Agent turn is not terminal server-side, or its frozen execution binding does not match the session's recorded binding
- **THEN** the server MUST reject the cleanup operation with a conflict exactly as before this change
