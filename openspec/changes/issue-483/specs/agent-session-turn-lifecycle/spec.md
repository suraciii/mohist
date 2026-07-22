### Requirement: Stable AgentSession and Turn identities
An AgentSession SHALL remain the stable logical conversation identified by `sessionId`. Each conversation execution SHALL be represented by a Turn identified by the unique pair `(sessionId, turnId)`, and completing a Turn MUST NOT close, replace, or change the AgentSession identity.

#### Scenario: A workflow Turn completes
- **WHEN** a TaskRun-owned Turn reports normal completion for a bound AgentSession
- **THEN** the Turn is recorded as completed and the same AgentSession remains available for a later Turn

#### Scenario: An AgentJob Turn fails
- **WHEN** an AgentJob-owned Turn reports a confirmed failure
- **THEN** the Turn is recorded as failed without changing the AgentJob's work-result authority or closing the AgentSession

### Requirement: Turn transcript lifecycle
Every Turn SHALL have exactly one `turn.started` fact containing its stable `turnId`, start time, origin, and first input. Every Turn SHALL have at most one `turn.finished` fact with exactly one outcome of `completed`, `failed`, or `stopped`; a failure payload MUST appear only for a failed outcome. All message, tool, usage, model, status, and compaction facts belonging to a Turn MUST carry that Turn's `turnId`.

#### Scenario: A runtime reports an ordinary completed Turn
- **WHEN** a runtime has accepted a Turn's initial input and later reports completion
- **THEN** the transcript contains one `turn.started`, Turn-scoped runtime facts, and one final `turn.finished` with outcome `completed`

#### Scenario: A duplicate finish conflicts with the recorded result
- **WHEN** a Turn that already has a `turn.finished` fact receives a different terminal result
- **THEN** the conflicting result is rejected and the original Turn outcome remains unchanged

### Requirement: Turn ordering and closure
Transcript sequence values SHALL be interpreted only within their `turnId`. Within a Turn, inputs MUST precede output caused by them, `turn.finished` MUST be the final fact, and no further input or runtime fact may be appended after it.

#### Scenario: Two Turns have overlapping local sequence values
- **WHEN** an AgentSession contains two completed Turns whose facts both use the same local sequence values
- **THEN** each Turn is reconstructed independently by `turnId` without comparing those sequence values across Turns

#### Scenario: A runtime fact arrives after Turn completion
- **WHEN** a runtime submits an input, message, tool, usage, model, status, or compaction fact for an already finished Turn
- **THEN** the fact is rejected and does not alter the completed transcript

### Requirement: Session activity, binding, and command projection
Session query and presentation surfaces SHALL independently expose `activity` as `idle`, `active`, or `unknown`; `binding` as `unbound`, `bound`, or `missing`; and distinct `currentTurn` and `latestTurn` summaries. A finished Turn SHALL make activity `idle`, retain the current Runtime binding, and remain visible as `latestTurn`; AgentSession itself MUST NOT expose a normal completed, failed, or closed status or `closedAt`.

#### Scenario: A failed Turn becomes the latest Turn
- **WHEN** a bound AgentSession's active Turn finishes with outcome `failed`
- **THEN** its activity is `idle`, its binding remains `bound`, and its latest Turn reports the failure while the AgentSession is not terminal

#### Scenario: A physical Runtime Session is missing
- **WHEN** the current Runtime binding cannot be resolved
- **THEN** the Session projects binding as `missing` and presents recovery or Reset without representing the logical AgentSession as closed

### Requirement: Activity-driven command eligibility
Follow-up eligibility SHALL be derived from Session activity, binding, and Runner availability, and Cancel eligibility SHALL target only `currentTurn`. Historical Turn outcomes MUST NOT disable future Follow-up or change TaskRun or AgentJob results. Compact and Reset MUST be blocked while activity is `active` or `unknown`.

#### Scenario: A completed Turn is followed by a new prompt
- **WHEN** a bound, Runner-available AgentSession is idle after a completed, failed, or stopped Turn
- **THEN** Follow-up is eligible to start a new Turn in that same AgentSession

#### Scenario: Runtime stop cannot be confirmed
- **WHEN** a Cancel request or runtime timeout cannot confirm whether the current Turn stopped
- **THEN** activity becomes `unknown`, Follow-up, Compact, and Reset are blocked, and no stopped or failed Turn result is fabricated

### Requirement: Uniform producer and consumer behavior
OpenCode and Pi, Workflow TaskRun execution, AgentJob execution, and user-initiated Session execution SHALL use the same Turn lifecycle, activity projection, and command eligibility semantics. Runtime process caching, eviction, and resource release MUST NOT be represented as logical AgentSession closure.

#### Scenario: Pi completes an AgentJob Turn
- **WHEN** Pi completes a Turn started for an AgentJob-owned AgentSession
- **THEN** it produces the same Turn lifecycle result and reusable Session state as an equivalent OpenCode or Workflow execution
