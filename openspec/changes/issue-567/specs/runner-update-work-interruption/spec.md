### Requirement: Managed Runner updates require a confirmed interrupt before activation

A managed `runner` or `full` update SHALL require, before any candidate activation or Runner service restart, a Runner identity resolved from the authoritative connected-Runner lookup and an authoritative interrupt confirmation returned by that exact Runner. The confirmation SHALL be accepted only when it names the same Runner identity, reports status `interrupted`, and carries a consistent affected-work inventory. An unconfirmed interrupt MUST change nothing: the candidate MUST NOT be activated, the Runner service MUST NOT be restarted, the staged release MUST be discarded, and the previously verified service state MUST remain in force.

#### Scenario: Confirmed interrupt precedes activation and restart

- **WHEN** a managed `runner` update completes and the Server confirms the interrupt for the resolved Runner identity
- **THEN** the update SHALL have performed the interrupt confirmation before writing any active target or restarting the Runner service
- **AND** the Runner service SHALL restart exactly once with the candidate identity

#### Scenario: Unconfirmed interrupt leaves the deployment untouched

- **WHEN** the interrupt endpoint is unreachable, returns an error, or returns a response the CLI cannot validate
- **THEN** the update SHALL refuse activation without restarting the Runner service
- **AND** the staged candidate release SHALL be removed
- **AND** the previously verified runtime targets SHALL remain active

#### Scenario: Interrupt response names a different Runner

- **WHEN** the interrupt confirmation names a Runner identity other than the one resolved from the authoritative lookup
- **THEN** the update SHALL treat the interrupt as unconfirmed and refuse activation and restart

### Requirement: A confirmed interrupt creates a durable update-operation fence immediately

When the Server confirms a Runner update interrupt, it SHALL durably create a Server-side update operation that carries its own stable identity and names every affected active Agent work at that moment — Workflow Agent tasks (WorkflowRun, task attempt, and work identity) and AgentJobs. The fence SHALL mark each named work *recoverably interrupted* as part of creating the update operation. This marking MUST be effective immediately at confirmation and MUST NOT depend on observing a Runner disconnect, waiting for a settlement timeout, or any later reconciliation. Work that was not active at confirmation MUST NOT be marked.

#### Scenario: Active Workflow Agent task is fenced at confirmation

- **WHEN** the interrupt is confirmed while a Workflow Agent task is executing on the Runner
- **THEN** the durable update operation SHALL name that task's WorkflowRun, task attempt, and work identity and mark it recoverably interrupted
- **AND** the marking SHALL be durable before the old Runner process stops

#### Scenario: Active AgentJob is fenced at confirmation

- **WHEN** the interrupt is confirmed while an AgentJob is running on the Runner
- **THEN** the durable update operation SHALL name that AgentJob's identity and mark it recoverably interrupted
- **AND** the AgentJob SHALL NOT be left to drift into a non-dispatchable state through disconnect observation alone

#### Scenario: Abrupt old-Runner loss after confirmation does not unfence the work

- **WHEN** the old Runner exits abruptly after the interrupt was confirmed
- **THEN** each affected work SHALL already be marked recoverably interrupted by the update operation
- **AND** the marking SHALL NOT be created or delayed by Runner-disconnect handling or settlement-deadline expiry

#### Scenario: Repeated interrupt requests are idempotent

- **WHEN** the update interrupt is requested again for the same Runner before activation
- **THEN** the Server SHALL return the same interrupt confirmation for the existing update operation
- **AND** it SHALL NOT create a second update operation or duplicate any affected-work marking

### Requirement: The update stops the old Runner promptly without waiting for Agent turns to finish

A confirmed update interrupt SHALL stop Runner admission immediately: from confirmation until shutdown the old Runner MUST NOT claim or begin new work. The update flow SHALL stop the old Runner through a bounded cooperative interruption of in-flight Agent work; it MUST NOT wait for long-running Agent turns to complete naturally before the service restart, and the restart SHALL NOT be delayed by the remaining duration of an interrupted turn. The bounded shutdown SHALL include a bounded handoff through which the old Runner learns of a pending update operation; a handoff that does not complete within its bound SHALL NOT delay the restart.

#### Scenario: Admission closes at confirmation

- **WHEN** the interrupt has been confirmed and the old Runner is still running
- **THEN** the old Runner SHALL NOT claim or start any new dispatch
- **AND** the Server SHALL NOT dispatch new work to the draining Runner

#### Scenario: A long Agent turn does not delay the restart

- **WHEN** an in-flight Agent turn would otherwise continue executing for a long time
- **THEN** the update SHALL bound the old Runner's shutdown through cooperative interruption of that turn
- **AND** the Runner service restart SHALL proceed without waiting for the turn to finish naturally

#### Scenario: The shutdown handoff is bounded

- **WHEN** the old Runner cannot obtain the pending update operation from the Server within the handoff budget during shutdown
- **THEN** the Runner service restart SHALL proceed without waiting beyond the bounded shutdown
- **AND** the affected in-flight work SHALL keep its unresolved `started` fence instead of blocking the restart
