### Requirement: Durable Manager receipt liveness
Each accepted Manager Slack message SHALL project receipt liveness onto the originating Slack message through the durable Slack outbox. Manager receipt, progress, reply, and terminal rows SHALL use `SlackDeliveryOwnerIds.ManagerProjectId`, `SlackDeliveryOwnerKinds.Manager`, and the Enrollment id as owner id. The receipt projection SHALL use a stable dispatch identity derived from the immutable Manager origin, so duplicate or replayed ingress events are idempotent.

#### Scenario: Manager message is accepted
- **WHEN** an authorized Manager direct message is accepted into the durable inbox
- **THEN** the system enqueues exactly one receipt reaction on the originating Slack message and records enough durable origin information to recover that projection

#### Scenario: Manager message is replayed
- **WHEN** the same Manager event is delivered more than once or an accepted inbox row is redriven
- **THEN** receipt processing reuses the existing durable dispatch identity and does not add another receipt reaction or another Manager execution

### Requirement: Manager progress projection
When a Manager execution is queued or actively running, the system SHALL project progress using the ordinary Slack Agent liveness lifecycle. Progress SHALL target the authoritative Manager reply anchor, replace prior replaceable progress for the same execution, and SHALL remove the receipt state before presenting the working state when both transitions are required.

#### Scenario: Manager execution begins
- **WHEN** the accepted Manager execution is queued or starts running and progress is applicable
- **THEN** the system removes the receipt reaction, adds the working reaction, and maintains one replaceable progress projection for that execution and Slack origin

#### Scenario: Manager execution completes before progress is emitted
- **WHEN** a Manager execution reaches a terminal state before a working projection is stored
- **THEN** the liveness projection still converges to a terminal reaction for the originating message without leaving a receipt reaction as the final state

#### Scenario: Progress is updated repeatedly
- **WHEN** retries, duplicate progress events, or recovery emit progress for an execution already showing progress
- **THEN** the durable outbox replaces or deduplicates the existing progress projection and does not create competing working messages or reactions

### Requirement: Exactly one terminal reaction
Every Manager execution SHALL close with exactly one terminal reaction for each of `completed`, `failed`, `cancelled`, and `unknown` outcomes. The terminal reaction SHALL be a success reaction for `completed` and an attention reaction for all other terminal outcomes, and the working reaction SHALL be removed before or as part of terminal convergence.

#### Scenario: Manager execution succeeds
- **WHEN** the durable terminal outcome is `completed`
- **THEN** the originating Slack message has one terminal success reaction, no active working reaction, and no duplicate terminal mutation for that execution

#### Scenario: Manager execution fails or is cancelled
- **WHEN** the durable terminal outcome is `failed` or `cancelled`
- **THEN** the originating Slack message has one terminal attention reaction, no active working reaction, and no Server-authored fallback reply is required to close liveness

#### Scenario: Manager execution has an unknown outcome
- **WHEN** the durable terminal outcome is `unknown`
- **THEN** the originating Slack message has one terminal attention reaction, no active working reaction, and the outcome remains distinguishable as unknown for recovery and operator handling

### Requirement: Recovery and terminal delivery are idempotent
Manager liveness SHALL be driven from durable inbox, Session, execution, and outbox facts and SHALL converge after process restart, Runner recovery, terminal delivery redelivery, and duplicate or replayed terminal events. Recovery MUST NOT create a second terminal reaction or leave the originating message stuck in receipt or working state.

#### Scenario: Terminal delivery is redelivered
- **WHEN** the same Manager terminal delivery event is handled more than once
- **THEN** the liveness store and outbox retain one terminal projection for that execution and do not enqueue a second terminal reaction

#### Scenario: Execution is recovered after an interruption
- **WHEN** a Manager execution is recovered with a new runtime execution and reaches a known or unknown terminal outcome
- **THEN** the recovered execution uses the same durable Slack origin, completes the pending liveness lifecycle, and leaves exactly one terminal reaction for the logical execution

#### Scenario: Process restarts with pending liveness work
- **WHEN** the Server or Slack adapter restarts while receipt, progress, reaction removal, or terminal delivery is pending
- **THEN** durable recovery resumes the pending projection, preserves dispatch deduplication, and eventually closes the logical execution with the required terminal reaction

### Requirement: Reply and liveness share ownership and origin
Manager Agent reply actions and Manager liveness projections SHALL use the same durable Slack origin and outbox idempotency rules as ordinary Agent Connections while using the explicit Manager project and Manager owner. The system SHALL authenticate replies through the separate Manager reply route, allow a valid reply action to promote or complete only the matching Manager progress message, and MUST NOT create a second logical terminal liveness lifecycle.

#### Scenario: Manager Agent sends its terminal reply
- **WHEN** the Manager Agent sends a reply using the authoritative Slack reply action before terminal liveness finalization
- **THEN** the separate reply route validates workspace, conversation, thread, triggering message, actor, Enrollment, Session, and dispatch against the immutable origin, writes the Manager-owned outbox row, promotes or deduplicates the matching progress projection, and terminal reaction finalization still produces exactly one terminal reaction

#### Scenario: Manager reply is sent twice
- **WHEN** the same execution repeats a reply request after a timeout or client retry
- **THEN** the Manager outbox uses the execution idempotency key to retain one progress/terminal row and does not append a duplicate message or create a second liveness lifecycle

#### Scenario: Manager Agent sends no terminal reply
- **WHEN** the Manager Agent completes without sending a reply action
- **THEN** reaction liveness still closes for the durable outcome, while no Server-generated text reply is created solely to compensate for the missing Agent action
