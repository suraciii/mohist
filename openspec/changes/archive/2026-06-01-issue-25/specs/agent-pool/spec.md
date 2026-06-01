## ADDED Requirements

### Requirement: Runner polling repairs stale workflow claims
Runner polling SHALL NOT retain runner assignments or backlog running claims for workflows that cannot provide runnable work after being claimed. If a runner claims a workflow and `GetWorkAsync` returns no work, the runner MUST release its assignment and repair or release the backlog claim before returning capacity to the pool.

#### Scenario: Claimed workflow returns no work
- **WHEN** `RunnerGrain.PollAsync` claims a workflow from the backlog
- **AND** the claimed workflow returns no work from `GetWorkAsync`
- **THEN** the runner SHALL remove the workflow from its assigned workflow set
- **AND** the backlog running claim SHALL be released or repaired so the workflow is not left claimed by that runner
- **AND** the workflow SHALL NOT consume runner capacity after the poll returns

#### Scenario: Poll repair records stale-state diagnostics
- **WHEN** runner polling repairs a claimed workflow because no work is available
- **THEN** the system SHALL record diagnostic evidence identifying the workflow, runner, and stale-claim reason
- **AND** the repair SHALL leave persisted scheduling state consistent with the workflow's current runnable status

### Requirement: Runner assignments reflect active leases only
The agent pool SHALL treat a workflow as assigned to a runner only while the workflow has an active lease and runnable work. Runner assignment tracking MUST be cleared when the corresponding backlog claim or workflow lease is released.

#### Scenario: Lease release clears runner assignment
- **WHEN** an active workflow lease is cleared because the workflow is paused, cancelled, failed, completed, or stale
- **THEN** the runner assignment for that workflow SHALL be removed
- **AND** runner status SHALL NOT report the workflow as running unless a new active lease is created
