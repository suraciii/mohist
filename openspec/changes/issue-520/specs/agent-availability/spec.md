### Requirement: Availability is a Server conclusion independent of Readiness

The Server SHALL compute an Agent Availability conclusion that answers whether an execution for that Agent can start now or must wait. Availability SHALL be independent of Readiness: a `Ready` Agent can be unavailable, and availability facts (runner offline, capacity full, concurrency limit) SHALL NOT be presented as Readiness outcomes or setup gaps.

#### Scenario: Ready but unavailable
- **WHEN** an Agent is `Ready` and no runner is online
- **THEN** Availability SHALL indicate the Agent must wait, and Readiness SHALL remain `Ready`

#### Scenario: Ready and can start
- **WHEN** an Agent is `Ready`, at least one runner is online with a free slot, and the Agent is under its concurrency limit
- **THEN** Availability SHALL indicate the Agent can start now

### Requirement: Availability distinguishes the waiting reason

When an Agent cannot start now, Availability SHALL name why: no online runner, runner capacity full, or the Agent's concurrency limit reached.

#### Scenario: Reason is no online runner
- **WHEN** no runner is online
- **THEN** Availability SHALL report the waiting reason as no online runner

#### Scenario: Reason is capacity full
- **WHEN** runners are online but all runner slots are in use
- **THEN** Availability SHALL report the waiting reason as capacity full

#### Scenario: Reason is concurrency limit
- **WHEN** the Agent has reached its MaxConcurrentRuns limit
- **THEN** Availability SHALL report the waiting reason as concurrency limit

### Requirement: Work that cannot start due to runner or capacity waits rather than failing

Submitted work that cannot start because no runner is available or runner capacity is full SHALL NOT terminally fail with a `runner-unavailable` verdict. It SHALL remain in a waiting state and proceed when a runner and slot become available, or until it is cancelled. (Waiting caused by the Agent concurrency limit is specified under `agent-concurrency`.)

#### Scenario: No runner does not terminally fail the job
- **WHEN** an AgentJob is submitted while no runner is online
- **THEN** the AgentJob SHALL wait rather than enter a terminal `Failed` state with reason `runner-unavailable`

#### Scenario: Work proceeds when capacity returns
- **WHEN** a waiting AgentJob exists and a runner with a free slot comes online
- **THEN** the AgentJob SHALL proceed to execution without being resubmitted by the user

### Requirement: Waiting work and its reason are visible

A submitted work item that is waiting to start SHALL be visible to the user and SHALL state what it is waiting for (no online runner, capacity full, or concurrency limit). The waiting state SHALL be distinguishable from executing and from terminal.

#### Scenario: User sees a waiting job and its reason
- **WHEN** a submitted work item is waiting because no runner has a free slot
- **THEN** the user SHALL be able to see that the work item is waiting and that the reason is capacity full

### Requirement: Availability is the Server's unified conclusion

Web and CLI SHALL present the Server's Availability conclusion and waiting reasons, and SHALL NOT synthesize availability or capacity verdicts from raw runner data.

#### Scenario: Clients present the server conclusion
- **WHEN** the Web or CLI shows Agent availability or a waiting work item
- **THEN** it SHALL display the Server-provided conclusion and reason rather than a client-synthesized substitute
