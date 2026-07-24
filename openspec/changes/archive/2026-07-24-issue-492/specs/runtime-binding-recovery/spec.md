### Requirement: A still-queryable Runtime Session is never classified as missing or replaced

A physical Runtime Session that remains queryable on its owning Runner SHALL NOT be classified as missing and SHALL NOT be replaced during task start, retry, Follow-up, Compact, Cancel, or reconnect recovery. The resolve step SHALL return ready with the binding unchanged whenever the owning Runner confirms the bound Runtime Session still exists. Only a confirmed-missing result from the owning Runner SHALL authorize recovery.

#### Scenario: A still-queryable session is retained at task start

- **WHEN** a task starts against an AgentSession whose bound Runtime Session still exists on its owning Runner
- **THEN** the resolve step SHALL return ready with the binding unchanged
- **AND** SHALL NOT create a replacement candidate Session

#### Scenario: A still-queryable session is retained on retry

- **WHEN** a task retries against an AgentSession whose bound Runtime Session still exists on its owning Runner
- **THEN** the resolve step SHALL return ready with the binding unchanged
- **AND** SHALL NOT create a replacement candidate Session

#### Scenario: A still-queryable session is retained on Follow-up

- **WHEN** a Follow-up is dispatched against an AgentSession whose bound Runtime Session still exists on its owning Runner
- **THEN** the resolve step SHALL return ready with the binding unchanged
- **AND** SHALL NOT create a replacement candidate Session

#### Scenario: A still-queryable session is retained on Compact

- **WHEN** a Compact is issued against an AgentSession whose bound Runtime Session still exists on its owning Runner
- **THEN** the Runtime Session SHALL NOT be classified as missing or replaced
- **AND** SHALL NOT be replaced with a new empty Session

#### Scenario: A still-queryable session is retained on Cancel

- **WHEN** a Cancel is issued against an AgentSession whose bound Runtime Session still exists on its owning Runner
- **THEN** the Runtime Session SHALL NOT be classified as missing or replaced
- **AND** SHALL NOT be replaced with a new empty Session

#### Scenario: Only confirmed-missing authorizes recovery

- **WHEN** a probe returns a result other than confirmed-missing (for example, present, active, or unavailable)
- **THEN** recovery SHALL NOT be authorized
- **AND** the current binding SHALL be preserved

### Requirement: Physical-Session existence checks use the type-checked SDK request contract

Every physical Runtime Session existence check SHALL be routed through the official, type-checked Runtime SDK request contract for the bound runtime. The existence check SHALL NOT use untyped escape hatches that could hide SDK DTO drift. A misclassification caused by a malformed or drifted existence response SHALL be impossible to hide behind an untyped cast.

#### Scenario: Existence checks are type-checked against the SDK contract

- **WHEN** the resolve step checks whether a bound Runtime Session exists on its owning Runner
- **THEN** the check SHALL be expressed through the official, type-checked SDK request contract for that runtime
- **AND** SHALL NOT rely on an untyped cast that suppresses SDK DTO mismatch

### Requirement: Confirmed-missing recovery creates at most one candidate and submits input once

Confirmed-missing recovery SHALL require an `idle` AgentSession and an unchanged expected binding. Recovery SHALL create at most one candidate Runtime Session, SHALL confirm the replacement binding, and SHALL submit the triggering input exactly once. Repeated retry or reconnect attempts SHALL NOT leave multiple empty candidate Sessions.

#### Scenario: Recovery requires idle and an unchanged binding

- **WHEN** confirmed-missing recovery is attempted for an AgentSession whose activity is not `idle`
- **THEN** recovery SHALL be rejected with an activity conflict
- **AND** SHALL NOT create a candidate or change the binding

#### Scenario: Recovery creates at most one candidate

- **WHEN** confirmed-missing recovery is attempted and the expected binding is still current
- **THEN** recovery SHALL create at most one candidate Runtime Session
- **AND** SHALL confirm the replacement binding before submitting input

#### Scenario: Repeated attempts leave no multiple empty candidates

- **WHEN** confirmed-missing recovery is retried or re-triggered by a reconnect for the same binding
- **THEN** at most one candidate Runtime Session SHALL have been created for the expected binding
- **AND** SHALL NOT accumulate additional empty candidate Sessions

### Requirement: Non-recovery conditions preserve the binding and never replay input

A timeout, transport failure, unavailable runtime, corrupt response, uncertain input acceptance, an `active` activity, or an unresolved `unknown` activity SHALL be treated as an explicit failure that preserves the current binding and SHALL NEVER replay or re-submit the triggering input. `unknown` SHALL NOT be simplified to `idle` to permit recovery.

#### Scenario: Timeout preserves the binding and does not replay input

- **WHEN** a resolve, turn, or recovery attempt times out
- **THEN** the current binding SHALL be preserved
- **AND** the triggering input SHALL NOT be replayed

#### Scenario: Transport failure preserves the binding and does not replay input

- **WHEN** a resolve, turn, or recovery attempt fails with a transport error
- **THEN** the current binding SHALL be preserved
- **AND** the triggering input SHALL NOT be replayed

#### Scenario: Unavailable runtime preserves the binding and does not replay input

- **WHEN** the owning Runner's runtime is unavailable for a resolve or recovery attempt
- **THEN** the current binding SHALL be preserved
- **AND** the triggering input SHALL NOT be replayed

#### Scenario: Corrupt response preserves the binding and does not replay input

- **WHEN** a resolve or recovery attempt receives a corrupt or unclassifiable response
- **THEN** the current binding SHALL be preserved
- **AND** the triggering input SHALL NOT be replayed

#### Scenario: Active activity blocks recovery and does not replay input

- **WHEN** recovery is attempted against an AgentSession whose activity is `active`
- **THEN** the current binding SHALL be preserved
- **AND** the triggering input SHALL NOT be replayed

#### Scenario: Unresolved unknown blocks recovery and does not replay input

- **WHEN** recovery is attempted against an AgentSession whose activity is unresolved `unknown`
- **THEN** the current binding SHALL be preserved
- **AND** the triggering input SHALL NOT be replayed

### Requirement: Superseded-binding facts cannot change the current state

A fact or event reported against a Runtime Session binding that is no longer current SHALL NOT change the AgentSession's current binding, activity, transcript, or accumulated usage. Reconnect recovery joins task start and idle-Follow-up input as sanctioned recovery triggers; each SHALL be gated on the unchanged expected binding.

#### Scenario: An old-binding recovery request is rejected

- **WHEN** a confirmed-missing recovery request carries an expected binding that no longer matches the current binding
- **THEN** the request SHALL be rejected as stale
- **AND** the current binding, activity, transcript, and accumulated usage SHALL remain unchanged

#### Scenario: Reconnect recovery is gated on the unchanged binding

- **WHEN** reconnect recovery is authorized for an AgentSession
- **THEN** it SHALL proceed only while the expected binding is still current
- **AND** SHALL be rejected once the binding has been superseded
