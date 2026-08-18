### Requirement: Interruption and recovery transitions are exposed per affected work

The AgentSession, AgentTurn, and workflow-task read models and API DTOs SHALL expose the update-caused interruption lifecycle for every work named by a durable update operation: *interrupting* / *interrupted* at fence creation, *recovering* once a replacement attempt is allocated, and *recovered* once the replacement execution settles. Each exposed transition SHALL carry the update-operation identity and the affected work identity, SHALL be derived from the durable update operation and receipt-arbitration records, and SHALL NOT be inferred from transport failures or idle observations. The original attempt SHALL remain visible as interrupted history alongside the replacement.

#### Scenario: Executing turn becomes interrupting and interrupted

- **WHEN** an update interrupt is confirmed while an Agent turn is executing for an affected work item
- **THEN** the session, turn, and workflow-task read models SHALL expose the interrupting and interrupted states with the update-operation identity
- **AND** this SHALL happen from fence creation, before the old Runner stops, rather than after a disconnect or timeout

#### Scenario: Replacement allocation is visible as recovering

- **WHEN** the Server accepts a confirmed interruption receipt and allocates a replacement attempt
- **THEN** the read models SHALL expose a recovering state carrying the new recovery generation and replacement turn identity
- **AND** the original attempt SHALL remain visible as interrupted history

#### Scenario: Replacement completion is visible as recovered

- **WHEN** the replacement execution returns an authoritative result that the Server settles
- **THEN** the read models SHALL expose the affected work as recovered
- **AND** the recovered state SHALL reference the update operation that caused the interruption

#### Scenario: Unaffected work shows no interruption states

- **WHEN** work was not active at the interrupt confirmation and is not named by the durable update operation
- **THEN** its session, turn, and task read models SHALL NOT expose interruption or recovery states

### Requirement: Update-caused stop failures are reported as actionable states

A stop or cleanup failure caused by the Runner update — including a runtime abort whose transport fails because the old runtime host is shutting down — SHALL be surfaced with update context: the update operation, the affected work identity, the current interruption state, and the expected recovery path. The system MUST NOT surface such failures as raw fetch or transport errors (for example a `session.abort fetch failed` style message) for work named by an update operation.

#### Scenario: Runtime abort transport fails during update shutdown

- **WHEN** the physical stop call for an affected turn fails because the old runtime is shutting down for a confirmed update
- **THEN** the surfaced failure SHALL name the update interruption, the affected work, and its state
- **AND** it SHALL NOT present the underlying fetch or transport error text as the user-facing state

#### Scenario: Web presentation renders the interruption lifecycle

- **WHEN** a session or workflow view includes work in an interrupting, interrupted, recovering, or recovered state
- **THEN** the web UI SHALL render that state with its update context
- **AND** it SHALL NOT render an un-actionable runtime error for that work

### Requirement: Interruption visibility is idempotent under replay

Projections of interruption and recovery states SHALL be idempotent: replayed events, duplicate receipts, and repeated reconciliation for the same update operation SHALL NOT produce duplicate transitions or oscillation between states, and each work's exposed state SHALL reflect the latest durable transition for its recovery generation.

#### Scenario: Duplicate update events are projected once

- **WHEN** the same interruption or recovery event is delivered more than once to the read models
- **THEN** the projection SHALL expose a single transition
- **AND** the exposed state SHALL remain the latest durable transition for that work
