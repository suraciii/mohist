### Requirement: Workflow reports preserve Provider rate-limit outcomes
The Runner-to-Server Workflow report contract SHALL carry a bounded Provider throttling expiry as the structured category `provider-rate-limited`, rather than reducing it to a generic failed result. The report SHALL preserve the canonical Provider identity and throttling facts supplied by execution. Genuine runtime and task failures SHALL retain their genuine failure categories and details.

#### Scenario: A bounded throttle is translated without loss
- **WHEN** a Runner reports a model turn whose bounded throttling wait expired
- **THEN** the Workflow report SHALL identify the outcome as `provider-rate-limited`
- **AND** SHALL preserve the Provider identity and actionable throttling facts
- **AND** SHALL NOT translate it only to `TaskReportStatus.Failed` with `FailureReason.TaskFailed`

#### Scenario: A genuine turn failure remains genuine
- **WHEN** a Runner reports a non-rate-limit runtime or task failure
- **THEN** the Workflow report SHALL preserve the existing genuine failure category and error details
- **AND** SHALL NOT relabel the report as `provider-rate-limited`

### Requirement: Workflow status distinguishes waiting, expiry, and ordinary failure
While a Workflow Agent request is waiting for Provider admission or rate-limit backoff, the task and run projections SHALL expose the nonterminal state `provider-rate-limit-waiting`. The waiting state SHALL retain the task as unfinished, SHALL NOT advance the stage, and SHALL NOT mark the run as an ordinary failure. When the bounded wait expires, projections SHALL expose the terminal category `provider-rate-limited`. Ordinary runtime and task failures SHALL retain their existing status and category.

#### Scenario: A throttled task is visible while waiting
- **WHEN** a Workflow Agent request is queued for Provider admission or is waiting under `Retry-After` or configured backoff
- **THEN** the Workflow task and run projections SHALL expose `provider-rate-limit-waiting`
- **AND** SHALL identify the affected Provider and known next-attempt or remaining-wait facts
- **AND** SHALL NOT expose the task as completed or as an ordinary failed task
- **AND** the Workflow stage SHALL NOT advance while the request is waiting

#### Scenario: Bounded expiry is distinct from task failure
- **WHEN** the Provider remains throttled until the configured wait bound expires
- **THEN** the Workflow task and run projections SHALL expose `provider-rate-limited`
- **AND** SHALL expose the Provider identity, latest throttling signal, and wait-bound or retry timing facts
- **AND** SHALL NOT expose the result only as `turn-failed` or ordinary `TaskFailed`

#### Scenario: A non-rate-limit failure remains ordinary
- **WHEN** a Workflow task fails because of a runtime error, invalid input, cancellation, or genuine task failure unrelated to Provider throttling
- **THEN** the task and run projections SHALL retain the existing failure status and reason
- **AND** SHALL NOT expose a Provider rate-limit waiting or expiry state

### Requirement: CLI and Web consumers expose actionable rate-limit state
CLI and Web Workflow consumers SHALL handle `provider-rate-limit-waiting` and `provider-rate-limited` explicitly. Structured output and user-facing status views SHALL expose the affected Provider and available wait, expiry, retry, or recovery facts instead of relying on a generic failed label or message. Consumers MUST NOT treat a waiting state as completed or immediately retryable.

#### Scenario: CLI and Web show an in-progress throttle
- **WHEN** a Workflow status response contains `provider-rate-limit-waiting`
- **THEN** CLI table/JSON and Web task/run views SHALL render a throttled waiting state
- **AND** SHALL show the Provider and known next-attempt or remaining-wait information
- **AND** SHALL continue treating the Workflow as nonterminal

#### Scenario: CLI and Web show an expired throttle
- **WHEN** a Workflow status response contains `provider-rate-limited`
- **THEN** CLI table/JSON and Web task/run views SHALL render a distinct Provider rate-limit outcome
- **AND** SHALL show the Provider, latest throttling facts, bounded wait information, and next recovery action
- **AND** SHALL NOT present the outcome as an unexplained generic task failure

### Requirement: Recovery after expiry is explicit and re-enters normal admission
After a Workflow task reaches `provider-rate-limited`, the Workflow SHALL expose an actionable retry operation for the affected task or run. The Workflow MUST NOT automatically resubmit the task while it is waiting or expired. An operator retry SHALL re-enter normal Runner, Agent, and Provider admission paths and MUST NOT bypass the configured Provider limit.

#### Scenario: Waiting does not duplicate work
- **WHEN** a Workflow task is in `provider-rate-limit-waiting`
- **THEN** the Workflow SHALL continue its current wait or cancellation path
- **AND** SHALL NOT create a second task attempt or automatically dispatch a duplicate request
- **AND** the status SHALL provide waiting information rather than an immediate retry instruction

#### Scenario: Expiry provides an operator retry
- **WHEN** a Workflow task reaches `provider-rate-limited`
- **THEN** the Workflow status SHALL provide an explicit retry action for that task or run
- **AND** invoking the action SHALL create the normal retry transition without discarding recorded throttling facts
- **AND** the resulting execution SHALL be subject to Runner, Agent, and Provider admission limits again

### Requirement: Rate-limit outcome data is durable and contract-compatible
New rate-limit categories and throttling facts SHALL survive Runner-to-Server translation, Workflow persistence, and CLI/Web serialization without requiring a migration for already in-flight work. Existing successful reports and genuine failure reports SHALL remain deserializable and retain their prior meaning. Consumers SHALL handle the new category as a first-class outcome rather than falling back to generic task failure behavior.

#### Scenario: An expired report survives persistence and reload
- **WHEN** a Workflow receives and persists a `provider-rate-limited` report
- **AND** the Workflow status is loaded again through the API
- **THEN** the status SHALL retain the `provider-rate-limited` category
- **AND** SHALL retain the Provider and throttling facts needed for recovery
- **AND** CLI and Web consumers SHALL receive the same distinct category

#### Scenario: Existing reports remain readable
- **WHEN** a persisted or in-flight report uses the existing success or genuine failure contract
- **THEN** the Server SHALL deserialize and process it without migration or reinterpretation as a rate-limit outcome
- **AND** the status projection SHALL preserve its existing behavior
