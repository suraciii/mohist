### Requirement: Follow-up execution round is a stable AgentTurn

Each follow-up execution round SHALL be recorded as a stable `AgentTurn` subrecord on the AgentSession with a stable Id, the next sequence number, the ordered list of `SessionInput` Ids it consumes, and a status that progresses `queued → executing → terminal`. The AgentSession SHALL be the authority for follow-up turn status, mirroring how the launch turn is already tracked.

#### Scenario: Idle session starts a turn for a follow-up

- **WHEN** an idle AgentSession accepts a follow-up input
- **THEN** a new `AgentTurn` SHALL be created in `queued` status whose `InputIds` contains that input's Id
- **AND** the turn SHALL progress to `executing` when the Runtime begins processing it
- **AND** the turn SHALL progress to a terminal status (`completed`, `failed`, `cancelled`, or `unknown`) when the round ends

#### Scenario: Follow-up turn is a stable subrecord

- **WHEN** a follow-up turn has been created and the Server grain state is reloaded after a restart
- **THEN** the `AgentTurn` subrecord SHALL remain present with the same Id, sequence, input linkage, and status

### Requirement: A turn consumes one or more inputs in order

An `AgentTurn` SHALL consume one or more `SessionInput` records in submission order. Each accepted input SHALL belong to exactly one turn; no input SHALL be left unassigned or assigned to more than one turn.

#### Scenario: Additional inputs join the same turn

- **WHEN** a turn is queued and additional follow-up inputs are accepted before execution begins
- **THEN** those input Ids SHALL be appended to the same turn's `InputIds` in submission order

#### Scenario: Inputs always belong to exactly one turn

- **WHEN** an AgentSession holds several follow-up inputs and turns
- **THEN** every accepted input SHALL appear in exactly one turn's `InputIds`
- **AND** the input-to-turn linkage SHALL preserve submission order within each turn

### Requirement: Queueing during execution without interruption

Inputs submitted while a turn is executing SHALL be accepted and queued; they SHALL NOT interrupt the running turn, and SHALL NOT be dropped, overwritten, or merged into the executing input. Queued inputs SHALL join the current turn when the Runtime supports consuming additional input mid-turn, otherwise SHALL be assigned to a subsequent turn in order.

#### Scenario: Input arrives during an executing turn

- **WHEN** a turn is `executing` and a new follow-up input is accepted
- **THEN** the executing turn SHALL continue uninterrupted
- **AND** the new input SHALL be queued (joined to the current turn when supported, otherwise held for the next turn)
- **AND** the input SHALL NOT be dropped or merged into the executing input

#### Scenario: Queued input starts a new turn when the current turn ends

- **WHEN** a turn reaches a terminal state and one or more inputs were queued during its execution that the Runtime did not consume
- **THEN** a new `AgentTurn` SHALL be created for those inputs in order
- **AND** the new turn SHALL progress through `queued → executing → terminal`

### Requirement: Distinct input acceptance and turn execution state

Input acceptance (`Accepted`) and `AgentTurn` status (`queued`, `executing`, terminal) SHALL be separately observable on the AgentSession, so a user can distinguish "input accepted, pending processing" from "input is being executed".

#### Scenario: Accepted-but-pending input is distinguishable from executing

- **WHEN** an input has been accepted but its turn has not begun executing
- **THEN** the session observation SHALL show the input as `Accepted`
- **AND** SHALL show its turn as `queued` (pending), not `executing`

#### Scenario: Input being executed is distinguishable from pending

- **WHEN** an input's turn has begun executing
- **THEN** the session observation SHALL show that turn as `executing`
- **AND** the input's acceptance SHALL remain `Accepted` regardless of the turn status
