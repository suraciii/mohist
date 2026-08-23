### Requirement: The outbox exposes an event-driven delivery-completion wait for a cleanup predecessor's retained facts

The runtime-event outbox MUST expose a delivery-completion wait that, given the Workflow session identity (project, workflow run, session name), cleanup attempt, the preceding cleanup operation id when applicable, and a bounded budget, completes when every record retained for the immediately preceding turn has completed outbound delivery. For cleanup attempt 1, the predecessor set is the original turn's `workflow-session` records for the Workflow scheduling identity. For cleanup attempt N greater than 1, the predecessor set is the prior attempt's `workflow-cleanup` boundary record plus every `session-followup` record whose payload carries that boundary's deterministic cleanup operation id. The operation id contains workflow run, task run, work item, and attempt identity, so it uniquely correlates the Session-scoped records without needing to open the Workflow AgentSession first. This correlation MUST include the cleanup runtime input and terminal activity even though those records are targeted by AgentSession id and keyed for delivery by cleanup turn id. A record counts as delivery-complete when it is acknowledged and removed through its acknowledgement policy or terminally settled by the existing deterministic-binding-refusal settlement. When no predecessor record is retained at call time, the wait MUST complete immediately.

#### Scenario: Nothing is retained for the predecessor

- **WHEN** the delivery-completion wait is invoked and the outbox retains no record belonging to the immediately preceding turn
- **THEN** the wait MUST complete immediately without waiting for the budget to elapse

#### Scenario: Original-turn terminal facts complete later

- **WHEN** cleanup attempt 1 starts while the original Workflow turn's records are retained and undelivered, and their delivery acknowledgement settles later within the budget
- **THEN** the wait MUST complete at that settlement
- **AND** the wait MUST cover every retained `workflow-session` record for the Workflow scheduling identity, not only selected terminal event types

#### Scenario: Prior cleanup-turn terminal facts complete later

- **WHEN** cleanup attempt N greater than 1 starts while attempt N minus 1's `session-followup` runtime input or terminal facts remain retained, and their delivery acknowledgement settles later within the budget
- **THEN** the wait MUST remain pending until the prior cleanup boundary and all retained `session-followup` records carrying its cleanup operation id are settled
- **AND** it MUST NOT complete merely because no `workflow-session` or `workflow-cleanup` record remains under the Workflow scheduling identity

### Requirement: The wait is event-driven without polling or new server round-trips

The delivery-completion wait MUST be driven by the outbox's delivery settlement events. It MUST NOT evaluate outbox state on a polling timer, and introducing the wait MUST NOT add new server status round-trips to observe delivery completion.

#### Scenario: No polling while delivery is pending

- **WHEN** records for the awaited predecessor remain retained across an interval before their delivery settles
- **THEN** the wait MUST resolve from the delivery settlement itself
- **AND** it MUST NOT wake on a timer to re-evaluate the retained state
- **AND** it MUST NOT issue server requests to query session or delivery status

### Requirement: The wait is bounded and cancellable and does not mutate outbox state

The delivery-completion wait MUST enforce its budget. When the budget expires before delivery completes, the wait MUST fail with an error identifying the awaited Workflow session, cleanup attempt, preceding cleanup operation when present, and the exhausted budget, so callers can report structured evidence. An aborted caller signal MUST cancel the wait promptly. The wait MUST NOT remove, reorder, or mutate any retained record, and MUST NOT change acknowledgement policies, delivery ordering, batching, or retention.

#### Scenario: Budget exhausted before delivery completes

- **WHEN** the budget elapses while records for the awaited predecessor are still retained
- **THEN** the wait MUST fail
- **AND** the error MUST identify the awaited Workflow session, cleanup attempt, preceding cleanup operation when present, and budget that was exhausted

#### Scenario: Caller cancellation

- **WHEN** the caller aborts its signal while the wait is pending
- **THEN** the wait MUST stop promptly
- **AND** the retained records, their delivery ordering, and the outbox's acknowledgement behavior MUST remain unchanged
