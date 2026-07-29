### Requirement: Work owners maintain the dispatch ledger
Each WorkflowRun and AgentJob SHALL durably own its own dispatch lifecycle, runner assignment, readiness time, and reconstructable dispatch data. No Runner-owned state or persistence record SHALL duplicate a work item's dispatch or lifecycle state.

#### Scenario: AgentJob becomes ready for execution
- **WHEN** an AgentJob is admitted to an eligible runner
- **THEN** the AgentJob ledger SHALL persist its runner assignment, ready time, and dispatch data while the Runner retains no copy of that work

#### Scenario: Server restarts with assigned work
- **WHEN** a server or Runner activation is recreated while a WorkflowRun or AgentJob has assigned work
- **THEN** the next poll SHALL reconstruct the work exclusively from its owner's durable ledger

### Requirement: All work is delivered by polling
Workflow and AgentJob work SHALL be delivered only in responses to a runner poll. The dispatch service SHALL derive each response from durable owner state and the runner's reported in-flight and awaiting-acknowledgement work keys.

#### Scenario: AgentJob is ready on an idle runner
- **WHEN** a runner with available capacity polls and an AgentJob is ready for that runner
- **THEN** the poll response SHALL include the AgentJob dispatch without a prior push assignment to the Runner

#### Scenario: Delivery response is lost
- **WHEN** an owner has running work assigned to a runner and that work key is absent from the runner's next poll report
- **THEN** the dispatch service SHALL redeliver the owner's reconstructed dispatch in that poll response

### Requirement: Polling prioritizes recovery and enforces shared capacity
The dispatch service SHALL serve running work absent from the reported set before pending work. It SHALL then consider pending work already assigned to the polling runner before unassigned work, and SHALL order candidates at each level by readiness time across WorkflowRun and AgentJob work. A new claim SHALL verify live runner registration and shared capacity so the total running work assigned to that runner never exceeds its configured slots.

#### Scenario: Missing running work and pending work compete
- **WHEN** a runner poll reports a missing running AgentJob dispatch and there is pending WorkflowRun work
- **THEN** the response SHALL include the redelivery before claiming the pending work

#### Scenario: Runner capacity is exhausted
- **WHEN** a runner already has running work equal to its configured slots across both work owners
- **THEN** the poll SHALL not claim additional WorkflowRun or AgentJob work

#### Scenario: Admission capacity changes before claim
- **WHEN** an AgentJob passed runner eligibility checks but the selected runner has no capacity when it next polls
- **THEN** the AgentJob SHALL remain pending and SHALL be reconsidered by a later poll without losing its dispatch data

### Requirement: Owner claims are atomic and recoverable
Each owner SHALL atomically transition a claimed pending work item to running with the claiming runner identity. A successful claim followed by dispatch construction or delivery failure SHALL leave the work running so a later poll can reconstruct and redeliver it. A deterministic invalid dispatch SHALL be rejected against the exact active work identity and SHALL transition only that work to failed.

#### Scenario: Concurrent claim wins elsewhere
- **WHEN** an owner cannot claim a pending item because its state changed or its workflow lock is unavailable
- **THEN** the dispatch service SHALL skip that item and continue evaluating other candidates without emitting a dispatch for it

#### Scenario: Retired action is discovered after claim
- **WHEN** dispatch construction identifies a retired action for a claimed work item
- **THEN** the owner SHALL mark that exact active work item failed and SHALL not redeliver it

### Requirement: Reports are delivered to the work owner idempotently
Runner result reports SHALL be routed directly to the owning WorkflowRun or AgentJob. An owner SHALL acknowledge both an accepted report and a report for work that is already terminal or no longer active as terminal acknowledgement; the runner SHALL stop retrying either acknowledgement.

#### Scenario: Report transport fails
- **WHEN** a runner completes work but its result report cannot be delivered
- **THEN** the runner SHALL retain the original result in its awaiting-acknowledgement set, include its work key in later poll reports, and retry the report until acknowledged

#### Scenario: Report arrives after closeout
- **WHEN** a runner reports a result for work already failed by runner-loss closeout
- **THEN** the owner SHALL return a stale acknowledgement and SHALL not change the terminal result

### Requirement: Runner owns presence, capacity, and closeout only
The Runner SHALL own registration information, poll-backed presence, configured slots, and closeout of running work, but SHALL not own queued, pending, or running work records. Active-work status and task-log authorization SHALL be derived from WorkflowRun and AgentJob ledgers.

#### Scenario: Runtime state is requested
- **WHEN** runner runtime state is queried
- **THEN** its active work list SHALL be assembled from running work assigned to that runner in the WorkflowRun and AgentJob ledgers

#### Scenario: Runner becomes unavailable
- **WHEN** a runner unregisters or its poll presence expires
- **THEN** the Runner SHALL report `runner-lost` failure for every running work assigned to it, and each owner SHALL record its affected work as failed

### Requirement: AgentJob availability timeout is owner-controlled
An AgentJob that remains pending beyond its configured availability deadline SHALL fail with the unavailable-runner result from its own ledger. This timeout SHALL not depend on Runner-side dispatch retries, acceptance state, or work storage.

#### Scenario: No runner can claim an AgentJob
- **WHEN** an AgentJob remains pending past its availability deadline without being claimed
- **THEN** the AgentJob SHALL transition to failed with the unavailable-runner reason and SHALL no longer be returned by polls
