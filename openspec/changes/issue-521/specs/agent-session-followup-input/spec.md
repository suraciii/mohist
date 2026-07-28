### Requirement: Follow-up acceptance creates a stable SessionInput

A follow-up accepted by Mohist SHALL persist a `SessionInput` subrecord on the AgentSession with a stable Id, the next session sequence number, the submitted text, a source, and `Acceptance = Accepted`. This record SHALL be persisted on the Server-owned session authority synchronously at acceptance time — it SHALL NOT depend solely on a flat transcript event written later by the Runner. The durable input identity SHALL exist before acceptance is reported to the caller.

#### Scenario: Accepted follow-up persists a stable input subrecord

- **WHEN** a client submits a follow-up with text to an AgentSession and Mohist accepts it
- **THEN** the AgentSession SHALL hold a new `SessionInput` subrecord with a stable Id, the next sequence number, the submitted text, and `Acceptance = Accepted`
- **AND** the subrecord SHALL be persisted before the acceptance is reported to the caller

#### Scenario: Follow-up input is not only a transcript event

- **WHEN** a follow-up is accepted
- **THEN** the input SHALL be queryable as a stable `SessionInput` subrecord on the AgentSession (with Id, sequence, and acceptance)
- **AND** SHALL NOT exist only as an un-keyed `session.input` transcript event whose identity is derived from event order

### Requirement: Accepted input durability

An accepted `SessionInput` SHALL survive Server process restart and SHALL NOT be silently dropped, overwritten, have its Id changed, or be merged into another input. When the session's input or turn queue is at capacity, Mohist SHALL reject new inputs rather than discard or merge an already-accepted input.

#### Scenario: Accepted input survives Server restart

- **WHEN** a follow-up input has been accepted and then the Server grain state is reloaded after a restart
- **THEN** the `SessionInput` subrecord SHALL remain present with the same Id, sequence, and text

#### Scenario: Capacity pressure does not drop an accepted input

- **WHEN** the AgentSession's queued inputs are at capacity and additional follow-ups arrive
- **THEN** the additional input SHALL be rejected
- **AND** the previously accepted `SessionInput` SHALL remain present and unchanged

### Requirement: Idempotent retry resolves to the same input

A follow-up submitted with a call identity (idempotency key) SHALL resolve to the same `SessionInput` on retry; a retry with the same key SHALL NOT create a second `SessionInput`. Two distinct call identities for the same text SHALL produce two distinct inputs.

#### Scenario: Retry with the same call identity returns the same input

- **WHEN** a follow-up with idempotency key `K` is accepted, and then resubmitted with the same key `K` after a lost response
- **THEN** the Server SHALL return the same `SessionInput` Id
- **AND** SHALL NOT create a second `SessionInput`

#### Scenario: Distinct call identities create distinct inputs

- **WHEN** the same follow-up text is submitted twice with two different idempotency keys
- **THEN** each submission SHALL create its own distinct `SessionInput` with its own Id

### Requirement: Follow-up does not create an AgentJob

A follow-up SHALL NOT create a new `AgentJob`. The launch `AgentJob` SHALL continue to own only the first execution; follow-up `SessionInput` and `AgentTurn` records SHALL carry no `JobId`. The AgentSession SHALL remain usable after the launch turn terminates.

#### Scenario: Follow-up after launch turn terminates creates no AgentJob

- **WHEN** the launch `AgentJob` has reached a terminal state and a follow-up is then accepted on the same AgentSession
- **THEN** no new `AgentJob` SHALL be created
- **AND** the new `SessionInput` SHALL NOT be linked to the launch `JobId`
