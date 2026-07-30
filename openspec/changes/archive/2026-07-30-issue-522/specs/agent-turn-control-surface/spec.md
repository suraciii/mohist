### Requirement: Cancel and stop address a single Turn

Every cancel and stop request SHALL identify exactly one target AgentTurn by its stable Turn id within the AgentSession. The operation SHALL affect only that Turn.

#### Scenario: Only the targeted Turn is affected
- **WHEN** a cancel or stop is issued carrying a specific Turn id
- **THEN** only the Turn with that id is evaluated for the operation, and no other Turn in the Session is cancelled or stopped

### Requirement: A stale entry reports the Turn has already ended

When a cancel or stop is issued against a Turn that has already reached a terminal state (completed, failed, cancelled, or stopped), the operation SHALL report that the Turn has already ended and SHALL NOT affect any Turn that started after the target Turn. In particular, a stale entry SHALL NOT cancel or stop the Turn currently executing.

#### Scenario: Stale cancel entry does not stop newer work
- **WHEN** a user invokes cancel against a Turn that already ended, while a later Turn is now executing
- **THEN** the operation reports that the target Turn has already ended and the currently executing Turn is neither stopped nor cancelled

#### Scenario: Stop an already-terminal Turn
- **WHEN** a stop is issued for a Turn that already reached a terminal state
- **THEN** the operation reports that the Turn has already ended and issues no runtime stop request

### Requirement: Web and CLI share one cancel and stop vocabulary

Web and CLI SHALL present cancel and stop outcomes using a single shared set of state labels — cancelled, stop-requested, stopped, and unknown — and SHALL explain each label with the same meaning on both surfaces. Both surfaces SHALL offer the same recovery or verification entry for an unknown stop. The CLI SHALL expose cancel and stop as distinct operations matching their distinct semantics: a deterministic cancel for a queued Turn, and a Runtime stop request for an executing Turn.

#### Scenario: CLI exposes distinct cancel and stop operations
- **WHEN** a user inspects the session control commands in the CLI
- **THEN** cancel and stop are available as separate operations whose help text distinguishes a deterministic cancel from a runtime stop request

#### Scenario: Web and CLI use the same label for each outcome
- **WHEN** a Turn is cancelled, stop-requested, stopped, or unknown
- **THEN** Web and CLI render the same state label for that outcome

#### Scenario: Web and CLI explain an unknown stop identically
- **WHEN** a stop ends unconfirmed and is viewed from both Web and CLI
- **THEN** both surfaces label it unknown, explain it the same way, and offer the same verification entry
