### Requirement: Follow-up uses the Pi steer channel while busy and the prompt channel while idle

The Pi runtime SHALL deliver a Follow-up to the current turn by injecting the user text into the running turn when the logical session has an active turn, and SHALL start a new user-initiated turn when the session is idle. The physical Pi Session binding SHALL NOT rotate on a Follow-up; the current `runtimeSessionId` SHALL remain unchanged in both cases.

#### Scenario: Follow-up joins an active Pi turn

- **WHEN** Follow-up is issued on a Pi session that currently has an active turn
- **THEN** the Pi runtime SHALL inject the user text into the active turn
- **AND** SHALL NOT start a new turn
- **AND** the physical Pi Session binding SHALL remain unchanged

#### Scenario: Follow-up starts a new turn when idle

- **WHEN** Follow-up is issued on a Pi session that is idle
- **THEN** the Pi runtime SHALL submit the user text to start a new turn
- **AND** SHALL NOT wait for that new turn to complete before reporting the Follow-up outcome
- **AND** the physical Pi Session binding SHALL remain unchanged

### Requirement: An idle Follow-up is accepted only after Pi reception is confirmed

For an idle Follow-up, the Pi runtime SHALL use Pi's reception confirmation as the point at which the Follow-up is accepted. If Pi rejects reception (for example because the model or provider credentials are missing or invalid), the Pi runtime SHALL return the Follow-up as a command failure carrying that rejection, and MUST NOT report the Follow-up as accepted. The Pi runtime MUST NOT automatically retry or replay an idle Follow-up whose reception was rejected.

#### Scenario: Idle Follow-up accepted after Pi reception

- **WHEN** an idle Follow-up is submitted and Pi confirms reception
- **THEN** the Follow-up SHALL be reported as accepted
- **AND** the new turn SHALL continue to progress through the existing session event channel

#### Scenario: Idle Follow-up rejected when Pi rejects reception

- **WHEN** an idle Follow-up is submitted and Pi rejects reception (for example missing model or credentials)
- **THEN** the Pi runtime SHALL return a Follow-up failure carrying the rejection
- **AND** SHALL NOT report the Follow-up as accepted
- **AND** SHALL NOT automatically retry or replay the Follow-up

### Requirement: Compact uses Pi native compaction and preserves the session identity

Compact SHALL compact the Pi Session using Pi's native compaction operation. The Pi runtime MUST NOT substitute a synthetic summary or fabricated compaction when Pi compaction is unavailable or fails; a compaction failure SHALL be reported as a command failure. After Compact, the physical Pi Session identity (`runtimeSessionId`) SHALL be unchanged and the compacted transcript SHALL remain visible through the existing session event channel.

#### Scenario: Compact preserves the Pi session identity

- **WHEN** Compact is applied to an idle Pi session
- **THEN** the Pi runtime SHALL invoke Pi's native compaction
- **AND** the `runtimeSessionId` SHALL be unchanged after compaction
- **AND** compaction events SHALL be projected through the existing session event channel

#### Scenario: Compact failure is reported, never faked

- **WHEN** Pi native compaction fails or is unavailable
- **THEN** the Pi runtime SHALL return a Compact failure carrying the underlying error
- **AND** SHALL NOT synthesize a summary or fabricate a compaction record
- **AND** the `runtimeSessionId` SHALL remain unchanged

### Requirement: Reset creates a new empty Pi Session and appends lineage without migrating context

Reset SHALL establish a new, empty Pi Session in the same working directory by creating a new Pi session file, and SHALL replace the current binding with the new session file path only after it is successfully created. The Pi runtime SHALL carry the current model and thinking level onto the new session when they are available. Reset SHALL append a new lineage entry for the new physical binding and SHALL preserve the stable `sessionId`. The Pi runtime MUST NOT migrate any conversation context from the prior Pi Session into the new one, and the prior session file SHALL remain queryable for audit.

#### Scenario: Reset replaces the binding and appends lineage

- **WHEN** Reset is applied to an idle Pi session currently bound to session file `S1`
- **THEN** the Pi runtime SHALL create a new empty Pi session file `S2` in the same working directory
- **AND** SHALL replace the current binding with `S2`
- **AND** SHALL append a lineage entry for `S2`
- **AND** the stable `sessionId` SHALL be unchanged

#### Scenario: Reset does not migrate prior context

- **WHEN** Reset creates a new Pi session file `S2` replacing `S1`
- **THEN** the new session `S2` SHALL start with no conversation context carried over from `S1`
- **AND** the prior session file `S1` SHALL remain queryable for audit

#### Scenario: Reset carries the current model and thinking level

- **WHEN** Reset is applied to a Pi session that has a current model and thinking level set
- **THEN** the Pi runtime SHALL apply the same model and thinking level onto the new session

### Requirement: Cancel aborts the active turn and reports stop confirmation honestly

Cancel SHALL request interruption of the active Pi turn via the Pi abort operation. The Pi runtime SHALL determine whether the turn actually stopped by observing the Pi session's streaming state and event sequence, not merely from the resolution of the abort call. When stop cannot be confirmed, the Pi runtime SHALL report the cancel as interrupt-unconfirmed, and MUST NOT portray a possibly-still-running turn as safely stopped.

#### Scenario: Cancel confirms the turn stopped

- **WHEN** Cancel is requested on a Pi session with an active turn and the Pi session's streaming state and event sequence confirm the turn stopped
- **THEN** the Pi runtime SHALL report a confirmed cancel

#### Scenario: Cancel reports interrupt-unconfirmed when stop is unknown

- **WHEN** Cancel is requested on a Pi session with an active turn and the stop cannot be confirmed from the Pi session's streaming state or event sequence
- **THEN** the Pi runtime SHALL report the cancel as interrupt-unconfirmed
- **AND** SHALL NOT report the turn as safely stopped

### Requirement: A missing Pi session fails explicitly with a Reset hint

When the bound Pi session file does not exist or cannot be opened, any session command that requires a live Pi session (Follow-up, Compact, Cancel) SHALL fail explicitly with an error indicating the Pi session is missing and prompting a Reset. The Pi runtime MUST NOT silently create a new Pi session to mask the missing binding, and MUST NOT fabricate a continuous conversation. Reset is the recovery operation and is not subject to this requirement.

#### Scenario: Follow-up, Compact, or Cancel on a missing Pi session reports a Reset hint

- **WHEN** a Follow-up, Compact, or Cancel targets an AgentSession whose bound Pi session file does not exist or cannot be opened
- **THEN** the Pi runtime SHALL return a failure indicating the Pi session is missing
- **AND** the failure SHALL prompt a Reset
- **AND** the Pi runtime SHALL NOT create a new Pi session for that command
