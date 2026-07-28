### Requirement: Launch returns stable references for all launch facts

A successful Agent launch SHALL return stable references to its AgentJob, AgentSession, first SessionInput, and first AgentTurn. Each returned reference MUST identify the same resource that the corresponding Job, Session, Input, or Turn observation surface uses after the request has completed.

#### Scenario: CLI receives a successful launch result
- **WHEN** `mo agent launch` is accepted
- **THEN** its output contains the stable Job, Session, Input, and Turn references for that launch

#### Scenario: Web receives a replayed launch result
- **WHEN** Web retries an accepted launch with its original call identity
- **THEN** the returned Job, Session, Input, and Turn references match the original launch result

### Requirement: Observation keeps work, conversation, input, and turn facts separate

The observation surface SHALL present AgentJob status, AgentSession activity, first SessionInput acceptance, and first AgentTurn result as distinct authoritative facts. AgentJob status answers the first launch work result; AgentSession activity answers whether the conversation has unfinished work; Input acceptance answers whether Mohist rejected, durably accepted, or cannot yet confirm the prompt; and Turn result answers whether the accepted input is queued, executing, terminal, or unresolved. No client MUST infer one fact by substituting another.

#### Scenario: Job completes while the Session remains usable
- **WHEN** the first AgentTurn completes successfully and its AgentJob reaches a terminal success result
- **THEN** observation reports the Job result and the completed Turn while retaining the AgentSession as an available conversation

#### Scenario: Input is accepted before execution begins
- **WHEN** Mohist has durably accepted the first input but the Runner has not begun execution
- **THEN** observation shows accepted input and queued or pending Turn without reporting the AgentJob as completed or failed

#### Scenario: Input is rejected before acceptance
- **WHEN** a launch is rejected before Mohist accepts its first input
- **THEN** the caller receives the rejection and no Input or Turn observation is presented as accepted or pending

### Requirement: Unknown remains a recoverable authoritative state

When Mohist cannot confirm whether the first input was delivered to the Runtime or whether its Turn has stopped, observation SHALL report Unknown for the affected fact. Unknown MUST NOT be rendered as Failed, idle, or successful, and Mohist MUST NOT automatically submit another copy of the input to resolve uncertainty.

#### Scenario: Runtime delivery outcome is uncertain
- **WHEN** a Runner disconnects after the first input is submitted and before Mohist receives decisive Runtime evidence
- **THEN** observation reports Unknown for the affected input or turn and preserves the original input for reconciliation

#### Scenario: Unknown is reconciled
- **WHEN** Mohist later receives authoritative Runtime evidence for an Unknown launch input or turn
- **THEN** observation updates the affected fact to its confirmed state without creating another input or turn

### Requirement: Observation resumes from known references without a live connection

Given any returned Job, Session, Input, or Turn reference, Web and CLI SHALL be able to re-read the current launch state, replies, and transcript after client disconnection, response loss, Server restart, or Runner reconnect. This recovery MUST use Mohist's persisted state and MUST NOT require the original request, terminal session, or a continuously connected live stream.

#### Scenario: Client reconnects during execution
- **WHEN** a client disconnects while the first AgentTurn is executing and later reconnects with the returned Session reference
- **THEN** it can read the original launch's current state and transcript, including replies recorded before and after the reconnection

#### Scenario: Runner reconnects during execution
- **WHEN** the Runner reconnects while the first AgentTurn is unresolved
- **THEN** subsequent Runner facts reconcile the original Job, Input, and Turn instead of creating replacement launch work

### Requirement: Web and CLI use the same launch-state meaning and recovery guidance

Web and CLI SHALL use the same authoritative meaning for accepted, queued, executing, completed, failed, and Unknown launch facts. For each displayed state, both clients MUST provide the corresponding recovery path: wait or observe for accepted, queued, or executing work; read the result or transcript for terminal work; and re-read or retry with the original call identity for Unknown.

#### Scenario: Unknown is displayed by either client
- **WHEN** either Web or CLI observes Unknown for a launch fact
- **THEN** it identifies the fact as unresolved and directs the caller to re-read or retry with the original call identity rather than start a new launch

### Requirement: Launch observation does not create later conversation work

Reading launch facts, results, replies, or transcript SHALL not create another SessionInput, AgentTurn, or AgentJob. The launch observation surface MUST remain limited to the first input and first turn created by the launch.

#### Scenario: User reads an accepted launch repeatedly
- **WHEN** a user repeatedly reads a launch's Job, Session, Input, Turn, or transcript
- **THEN** every read returns observation only and no additional conversation work is created
