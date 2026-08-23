### Requirement: The outbox exposes an event-driven delivery-completion wait for a Workflow session's retained facts

The runtime-event outbox MUST expose a delivery-completion wait that, given a Workflow session identity (project, workflow run, session name — the session's scheduling identity) and a bounded budget, completes when every record retained for that logical Workflow session has completed outbound delivery. A record counts as delivery-complete when it is no longer retained by the outbox: acknowledged and removed through its acknowledgement policy, or terminally settled by the existing deterministic-binding-refusal settlement. When no records are retained for the session at call time, the wait MUST complete immediately.

#### Scenario: Nothing is retained for the session

- **WHEN** the delivery-completion wait is invoked and the outbox retains no records for that Workflow session
- **THEN** the wait MUST complete immediately without waiting for the budget to elapse

#### Scenario: Retained terminal facts complete later

- **WHEN** the Workflow session's terminal facts are retained and undelivered when the wait is invoked, and their delivery acknowledgement settles at a later instant within the budget
- **THEN** the wait MUST complete at that settlement
- **AND** the wait MUST cover every record retained for the session's scheduling identity, not only a subset of its terminal facts

### Requirement: The wait is event-driven without polling or new server round-trips

The delivery-completion wait MUST be driven by the outbox's delivery settlement events. It MUST NOT evaluate outbox state on a polling timer, and introducing the wait MUST NOT add new server status round-trips to observe delivery completion.

#### Scenario: No polling while delivery is pending

- **WHEN** records for the awaited Workflow session remain retained across an interval before their delivery settles
- **THEN** the wait MUST resolve from the delivery settlement itself
- **AND** it MUST NOT wake on a timer to re-evaluate the retained state
- **AND** it MUST NOT issue server requests to query session or delivery status

### Requirement: The wait is bounded and cancellable and does not mutate outbox state

The delivery-completion wait MUST enforce its budget. When the budget expires before delivery completes, the wait MUST fail with an error identifying the awaited Workflow session and the exhausted budget, so callers can report structured evidence. An aborted caller signal MUST cancel the wait promptly. The wait MUST NOT remove, reorder, or mutate any retained record, and MUST NOT change acknowledgement policies, delivery ordering, batching, or retention.

#### Scenario: Budget exhausted before delivery completes

- **WHEN** the budget elapses while records for the awaited Workflow session are still retained
- **THEN** the wait MUST fail
- **AND** the error MUST identify the awaited Workflow session and the budget that was exhausted

#### Scenario: Caller cancellation

- **WHEN** the caller aborts its signal while the wait is pending
- **THEN** the wait MUST stop promptly
- **AND** the retained records, their delivery ordering, and the outbox's acknowledgement behavior MUST remain unchanged
