## Requirements

### Requirement: Preview and launch share one resolver

The system MUST resolve saved defaults and explicit execution overrides with
the same Server resolver for preview and launch. A client MUST NOT receive a
preview tuple that the launch path would reinterpret.

#### Scenario: Preview reports saved defaults

- **GIVEN** an active Agent with a valid saved execution definition
- **WHEN** preview omits `execution`
- **THEN** the response reports the saved tuple and marks every field source
  as `saved`
- **AND** no Job, Session, Input, Turn, workspace, attachment, coordinator,
  or Runner record is created

#### Scenario: Preview reports explicit overrides

- **GIVEN** an active Agent with saved runtime `pi`
- **WHEN** preview supplies `execution.model` and
  `execution.reasoningEffort`
- **THEN** the response reports those fields as `override`
- **AND** unspecified fields remain the saved values

### Requirement: Exact tuple is fail-closed

The system MUST NOT replace an unavailable or incompatible explicit tuple with
the saved tuple, another runtime, another model, or another variant.

#### Scenario: Explicit incompatible runtime

- **GIVEN** preview or launch requests a runtime that the authoritative
  capability evaluator cannot confirm
- **WHEN** the request is evaluated
- **THEN** the response exposes `unknown` or the stable incompatibility state
  and an actionable gap
- **AND** no fallback tuple is persisted or claimed

### Requirement: Launch freezes the resolved definition

The system MUST persist the resolved execution definition before dispatch and
the Job MUST consume that snapshot rather than rereading mutable Agent state.

#### Scenario: Agent edit after launch

- **GIVEN** launch resolved an explicit model override
- **WHEN** the saved Agent is edited before Runner claim
- **THEN** the Job and Session startup retain the original resolved model and
  field source

### Requirement: Idempotency includes execution configuration

The canonical request fingerprint MUST include the normalized execution
override, preserving omitted versus explicit null and sorting object keys.

#### Scenario: Same key with changed override

- **GIVEN** an Idempotency-Key already accepted a launch with one execution
  override
- **WHEN** the same key is reused with a different override
- **THEN** the route returns `launch_idempotency_conflict`
- **AND** it does not create or mutate a Job, Session, Input, Turn, or Runner
  claim

### Requirement: Preview is side-effect free

Preview MUST be a read operation. It MAY validate referenced source records,
but MUST NOT bind attachments, provision a workspace, create durable launch
participants, or claim a Runner.

#### Scenario: Malformed preview

- **WHEN** preview receives a non-string runtime or an unsupported effort
- **THEN** it returns a stable validation error before participant creation
