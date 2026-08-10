### Requirement: The Agent is the authority for the launch execution tuple

For every saved-Agent launch, the Agent definition SHALL be the authoritative source of the final `runtime`, `model`, `reasoningEffort`, and runtime-specific `variant`. The server SHALL resolve that tuple before creating or submitting the AgentSession and AgentJob. A launch caller SHALL NOT override the Agent tuple through prompt, context, or an inline runtime option.

#### Scenario: A launch resolves the Agent-owned tuple

- **WHEN** a caller starts a saved Agent with a prompt and optional context
- **THEN** the server SHALL resolve runtime, model, reasoning effort, and variant from the Agent definition before creating execution state
- **AND** caller input SHALL not replace any of those Agent-owned values

#### Scenario: A launch cannot replace the Agent variant or effort

- **WHEN** a launch request contains a value intended to override the Agent's reasoning effort or runtime-specific variant
- **THEN** the request SHALL be rejected or the undeclared override SHALL be ignored according to the launch boundary contract
- **AND** the accepted launch SHALL use the Agent's resolved tuple

### Requirement: AgentJob and AgentSession snapshots are immutable

The accepted launch SHALL persist the resolved runtime, model, reasoning effort, and variant in the AgentJob execution snapshot and the associated AgentSession/dispatch snapshot. Retries, Runner redelivery, idempotent replay, and process-recovery paths SHALL reuse those persisted values without rereading the mutable Agent definition. Editing, archiving, or disabling the Agent after acceptance SHALL not change the accepted Job's execution facts.

#### Scenario: An Agent edit does not change a pending Job

- **WHEN** an Agent's runtime, model, reasoning effort, or variant is edited after its Job is accepted but before dispatch
- **THEN** the Job SHALL retain the values captured at acceptance
- **AND** the eventual dispatch SHALL use the original tuple

#### Scenario: Recovery reuses the first accepted snapshot

- **WHEN** the server or Runner recovers and redelivers a queued or in-flight AgentJob
- **THEN** recovery SHALL read the durable Job snapshot
- **AND** it SHALL not recompute the tuple from the current Agent definition

### Requirement: Runner delivery keeps effort and variant independent

The AgentJob dispatch envelope SHALL carry `reasoningEffort` and runtime-specific `variant` as separate fields. The selected runtime adapter SHALL apply each field to its corresponding runtime input, SHALL preserve an explicitly unset value as unset, and SHALL not use `variant` as an alias for reasoning effort. Execution diagnostics and terminal facts SHALL identify the requested or resolved model, reasoning effort, and variant independently.

#### Scenario: A Runner applies both independent values

- **WHEN** an AgentJob snapshot contains a model, a reasoning effort, and a runtime-specific variant
- **THEN** the Runner SHALL deliver the model, effort, and variant independently to the selected runtime
- **AND** a runtime variant value SHALL not overwrite, stand in for, or be reported as the reasoning effort

#### Scenario: An unset effort is not invented during dispatch

- **WHEN** the durable snapshot records reasoning effort as unset
- **THEN** the Runner SHALL preserve the unset state in its request or return the defined preflight result
- **AND** it SHALL not infer an effort from the variant or silently choose another effort

### Requirement: Launch and execution results expose the frozen tuple

Accepted launch responses, Job observations, AgentSession execution facts, and terminal Job results SHALL expose the frozen runtime, model, reasoning effort, and variant using the same field names and value vocabulary. The result SHALL describe the tuple that the Job was asked to execute and SHALL not replace it with a later Agent configuration.

#### Scenario: A completed Job reports its execution tuple

- **WHEN** a Runner completes an AgentJob
- **THEN** the launch observation and terminal result SHALL expose the Job's runtime, model, reasoning effort, and variant
- **AND** the values SHALL match the accepted launch snapshot

#### Scenario: A failed Job retains the requested tuple

- **WHEN** an AgentJob fails because the selected runtime or model is temporarily unavailable
- **THEN** the failure or waiting observation SHALL retain the requested runtime, model, reasoning effort, and variant
- **AND** it SHALL not report a substituted configuration

### Requirement: Temporary execution failure never triggers provider fallback

An AgentJob whose frozen tuple is valid but temporarily unavailable SHALL remain pending, waiting, or retryable according to the Job state machine. Retries SHALL target the same runtime, model, reasoning effort, and variant. A known invalid or incompatible tuple SHALL fail preflight without dispatch rather than being repaired by selecting a different tuple.

#### Scenario: A temporary runtime failure is retried unchanged

- **WHEN** the selected runtime is unavailable after the Job snapshot is persisted
- **THEN** the Job SHALL wait or retry with the same frozen tuple
- **AND** it SHALL not switch provider, model, effort, or variant

#### Scenario: A known incompatible tuple is rejected before execution

- **WHEN** the frozen model and reasoning effort are known to be incompatible before dispatch
- **THEN** the Job SHALL produce an explicit preflight failure
- **AND** the Runner SHALL not execute a substituted tuple
