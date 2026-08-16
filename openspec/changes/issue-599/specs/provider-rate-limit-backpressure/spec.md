### Requirement: Provider admission is configurable and keyed by canonical Provider identity
The Runner SHALL enforce a configurable maximum number of concurrent in-flight model requests for each canonical Provider identity. Requests from different Agents, Workflow runs, tasks, and supported runtimes SHALL share the same limit when they resolve to the same Provider identity. Requests for different Provider identities SHALL use independent limits.

#### Scenario: Concurrent Agents on one Provider share a limit
- **WHEN** the configured limit for Provider `P` is `1`
- **AND** two concurrent Workflow runs use different Agents that both resolve to Provider `P`
- **THEN** the Runner SHALL admit only one model request to `P`
- **AND** the other request SHALL wait for Provider admission
- **AND** the waiting request SHALL NOT send a model request while the first request is in flight

#### Scenario: Different Providers do not contend
- **WHEN** two concurrent Workflow Agent requests resolve to different Providers
- **AND** both Providers have available capacity
- **THEN** the Runner SHALL admit both model requests concurrently
- **AND** admission for one Provider SHALL NOT consume capacity for the other Provider

### Requirement: Provider admission surrounds each actual model request
The execution plane SHALL acquire Provider admission immediately before each actual model request, including a retry attempt, and SHALL release the admission when that request completes, fails, or is cancelled. A request SHALL NOT retain an in-flight Provider permit while waiting for retry backoff.

#### Scenario: A queued request starts after a permit is released
- **WHEN** a request is waiting because its Provider limit is full
- **AND** the in-flight request holding the permit completes
- **THEN** the waiting request SHALL become eligible to acquire admission
- **AND** the Runner SHALL send the request only after admission is acquired

#### Scenario: Cancellation removes a queued request
- **WHEN** a request is waiting for Provider admission
- **AND** its cancellation signal is raised before admission
- **THEN** the Runner SHALL stop waiting promptly
- **AND** SHALL NOT send a model request for that execution
- **AND** SHALL NOT leave a Provider permit occupied by the cancelled waiter

### Requirement: Provider admission composes with existing capacity gates
Provider admission SHALL be an additional execution constraint. It MUST NOT replace, bypass, or increase the existing Runner capacity limit or Agent-level concurrency limit.

#### Scenario: Runner capacity remains limiting
- **WHEN** the Runner has no available execution slot
- **AND** the selected Provider has unused Provider capacity
- **THEN** the work SHALL remain subject to the existing Runner capacity decision
- **AND** Provider admission SHALL NOT cause the work to bypass or increase Runner capacity

#### Scenario: Agent capacity remains limiting
- **WHEN** an Agent has reached its configured concurrency limit
- **AND** the selected Provider has unused Provider capacity
- **THEN** another execution for that Agent SHALL remain blocked by the existing Agent limit
- **AND** Provider admission SHALL NOT start that execution

### Requirement: Provider admission is shared by qualifying Workflow executions on one Runner execution plane
The Provider limiter SHALL be owned at a scope shared by all qualifying Workflow Agent executions handled by the same Runner execution plane. It MUST NOT be scoped only to an Agent, Workflow run, task attempt, runtime session, or runtime implementation.

#### Scenario: Separate Workflow runs cannot bypass a saturated Provider
- **WHEN** one Workflow run has an in-flight request for Provider `P`
- **AND** a second Workflow run using a different Agent requests another model turn from `P`
- **AND** the configured limit for `P` is full
- **THEN** the second run SHALL wait for the shared Provider admission
- **AND** it SHALL NOT create an avoidable burst of concurrent requests for `P`
