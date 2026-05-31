## ADDED Requirements

### Requirement: Work item start events require explicit lease ownership
Pipeline session event streams SHALL represent workflow work item starts as consequences of explicit lease ownership. A `workflow_task_started` or equivalent work-start event for a workflow work item MUST only be emitted after the runner has durable ownership of the matching active lease.

#### Scenario: Start event follows lease creation
- **WHEN** a runner receives a workflow task work item
- **THEN** the workflow lease for that runner and work item SHALL be durable before `workflow_task_started` is emitted
- **AND** the start event payload SHALL identify the same workflow run, work item id, and runner as the durable lease

#### Scenario: Duplicate start requires intervening transition
- **WHEN** a `workflow_task_started` event already exists for a workflow run, work item id, and runner while that work item remains actively leased
- **THEN** the system SHALL NOT emit another `workflow_task_started` event for a different runner for that same work item
- **AND** a different runner MAY start only after an intervening abandon, expiration, interruption, failure, retry, or handoff event has been durably recorded

#### Scenario: Handoff event precedes new owner start
- **WHEN** ownership of an active workflow work item transfers from one runner to another
- **THEN** the event stream SHALL record the abandonment, expiration, interruption, failure, retry, or handoff reason before the new runner's start event
- **AND** consumers SHALL be able to reconstruct that the two start events are separate attempts rather than simultaneous active owners
