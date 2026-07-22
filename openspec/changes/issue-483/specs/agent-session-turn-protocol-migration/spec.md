### Requirement: Complete legacy transcript conversion
The upgrade SHALL convert persisted AgentSession transcript history into the Turn protocol so that existing user and runtime conversation history remains present and historical execution results remain representable as Turn results. Converted facts MUST satisfy the target Turn lifecycle invariants.

#### Scenario: A persisted Session has a historical terminal execution
- **WHEN** the upgrade processes a transcript that contains a historical completed, failed, or cancelled Session-terminal fact
- **THEN** the resulting transcript preserves the conversation and represents that execution as one finished Turn with the corresponding target outcome

### Requirement: Durable pending-outbox conversion
The upgrade SHALL convert pending Runner runtime-event outbox records to the Turn and Follow-up protocol before those records are delivered. Conversion and subsequent retry MUST preserve each operation's identity and MUST NOT lose a pending fact or cause the Runtime to receive duplicate input.

#### Scenario: A pending Follow-up input is upgraded before upload
- **WHEN** an undelivered Runner outbox record represents a Follow-up at upgrade time
- **THEN** it is delivered or reconciled under its stable operation identity without creating a second Runtime input side effect

### Requirement: Single post-upgrade protocol
After the upgrade, all Server, Runner, API, and Web write and read paths SHALL use the Turn and Follow-up protocol exclusively. They MUST NOT accept, emit, project, or dual-write `session.closed`, `session.followup_completed`, or `session.followup_failed`.

#### Scenario: A runner completes a Turn after upgrade
- **WHEN** OpenCode or Pi reports the completion of a Turn after the upgrade
- **THEN** the transcript and live projections use `turn.finished` and contain none of the legacy terminal Session event names

#### Scenario: A legacy terminal event is submitted after upgrade
- **WHEN** a Server or Runner protocol entry point receives `session.closed`, `session.followup_completed`, or `session.followup_failed`
- **THEN** it rejects the event without creating a transcript fact or Session state transition

### Requirement: No mixed terminal semantics per AgentSession
An AgentSession SHALL have one authoritative interpretation of execution completion after upgrade: Turn outcomes determine historical execution results, while Session activity and Runtime binding determine current operability. Migration MUST NOT leave a Session whose consumers can derive conflicting terminal Session and Turn meanings from its persisted data.

#### Scenario: A migrated Session receives a new Follow-up
- **WHEN** a migrated AgentSession has a bound Runtime and its latest historical Turn is finished
- **THEN** it projects as reusable according to activity and binding and can start a new Turn without consulting a legacy Session-terminal result
