### Requirement: Each obstruction class has distinct, actionable feedback

The Agents surfaces (list, detail, and launch) SHALL distinguish at least these obstruction classes and present each with a next action the user can take, rather than a raw error code or log line: queue/back-pressure (runner capacity full, agent concurrency limit, dispatch-pending), Runner offline (no online runner), configuration gap (Needs setup), and execution-side unavailability (the configured execution backend cannot run). Each class SHALL be visually and textually distinguishable from the others, and SHALL NOT be collapsed into a single generic "failed" state.

#### Scenario: Queue/back-pressure is distinguished from a configuration gap

- **WHEN** an Agent cannot start now because of runner capacity full or agent concurrency limit
- **THEN** the surface SHALL present back-pressure as an Availability waiting state, and SHALL NOT present it as a Needs-setup or configuration gap

#### Scenario: Runner offline names the cause and the next action

- **WHEN** a launch or start attempt is obstructed because no runner is online
- **THEN** the surface SHALL name that no runner is available and SHALL state that a runner must be connected, distinct from a configuration-gap message

#### Scenario: Execution-side unavailability names recovery, not configuration

- **WHEN** the configured execution backend reports it cannot run
- **THEN** the surface SHALL present execution-side unavailability as waiting for recovery, and SHALL NOT present it as a Needs-setup gap

### Requirement: A configuration gap names what is missing and where to fix it

When an obstruction is a configuration gap (Readiness Needs setup, whether detected before launch or returned by the server on a launch attempt), the surface SHALL list each gap's message and the action to fix it, and SHALL point to the place where the gap is fixed. The feedback SHALL be specific to the reported gaps and SHALL NOT display a generic "needs setup" without the gaps.

#### Scenario: A launch-time Needs-setup result surfaces the specific gaps

- **WHEN** a launch attempt is rejected because the Agent needs setup, and the server returns one or more gaps
- **THEN** the surface SHALL display each returned gap's message and action, and SHALL point to where they are fixed

### Requirement: Feedback does not require reading raw logs

The Agents surfaces SHALL let a user determine the next action from the presented feedback alone. The user SHALL NOT need to open raw Server/Runner logs to distinguish queue, Runner offline, configuration gap, or execution-side failure, or to know what to do next.

#### Scenario: The next action is derivable without logs

- **WHEN** any of the obstruction classes occurs on the list, detail, or launch surface
- **THEN** the presented feedback SHALL state the class and a next action, sufficient to decide what to do without consulting raw logs
