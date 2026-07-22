### Requirement: AgentSession activity has exactly three values

An AgentSession SHALL expose one current activity value, and that value SHALL be exactly one of `idle`, `active`, or `unknown`. The activity SHALL be an authoritative field owned by the session aggregate, not a value derived from a time window over the last received event. `idle` means no input is confirmed still executing and a new execution, Compact, or Reset may begin. `active` means the Runtime is processing an accepted input; a Follow-up may join the current execution and Cancel may attempt to stop it. `unknown` means Mohist cannot confirm whether an input was accepted or whether execution has stopped, and it MUST NOT be treated as a safe idle.

#### Scenario: A newly created session starts idle

- **WHEN** an AgentSession is created
- **THEN** its initial activity SHALL be `idle`
- **AND** its current binding MAY be absent

#### Scenario: Activity is not derived from event recency

- **WHEN** the session has not received an event within a fixed time window
- **THEN** the exposed activity SHALL NOT automatically become `inactive` or any terminal value solely because of the elapsed time
- **AND** the activity SHALL remain the last authoritative value until an activity transition changes it

### Requirement: An execution outcome returns the session to idle

When a TaskRun, AgentJob, or Follow-up execution completes, fails, or is cancelled, the AgentSession activity SHALL return to `idle` and the current binding SHALL be preserved. An execution outcome SHALL NOT transition the AgentSession into any `completed`, `failed`, `stopped`, `cancelled`, or `closed` terminal lifecycle state, and the session SHALL remain eligible to begin a subsequent Follow-up without a Reset.

#### Scenario: A task execution completes

- **WHEN** a Workflow TaskRun execution on the session completes successfully
- **THEN** the session activity SHALL become `idle`
- **AND** the current runtime binding SHALL remain unchanged
- **AND** a subsequent Follow-up SHALL be accepted on the same session without a Reset

#### Scenario: An execution fails or is cancelled

- **WHEN** an AgentJob or Follow-up execution on the session fails or is cancelled
- **THEN** the session activity SHALL become `idle`
- **AND** the session SHALL NOT be marked as failed or closed
- **AND** the current runtime binding SHALL remain unchanged

### Requirement: Activity transitions are authoritative

The session SHALL transition activity only according to: `idle` plus an accepted input becomes `active`; `active` plus a follow-up accepted into the current execution stays `active`; `active` plus execution confirmed stopped becomes `idle`; `active` plus a stop result that is uncertain becomes `unknown`; `idle` plus an input acceptance that is uncertain becomes `unknown`; `unknown` plus runtime evidence resolves to `active` or `idle`. A Runtime process exit, cache eviction, or persisted-file cleanup SHALL NOT by itself change the activity or close the session.

#### Scenario: An input is accepted

- **WHEN** the session accepts an input while `idle`
- **THEN** the activity SHALL transition to `active`

#### Scenario: A stop result is uncertain

- **WHEN** the Runtime cannot confirm that an accepted input has stopped
- **THEN** the activity SHALL become `unknown`
- **AND** the session MUST NOT be treated as `idle` until runtime evidence resolves it

### Requirement: Consumers read current activity, not historical end facts

API responses, command eligibility, and the Session page SHALL determine the current AgentSession state and the availability of Follow-up, Compact, and Reset from the current activity value only. A consumer SHALL NOT scan transcript history for end facts to reconstruct a terminal status, and SHALL NOT refuse a Follow-up, Compact, or Reset because a prior execution completed, failed, or was cancelled.

#### Scenario: A follow-up is requested after an execution ended

- **WHEN** a Follow-up is requested on a session whose prior execution ended and whose activity is `idle`
- **THEN** the request SHALL be evaluated against the current activity
- **AND** SHALL NOT be rejected as inactive or terminal because of the prior execution's outcome

#### Scenario: A follow-up is requested while executing

- **WHEN** a Follow-up is requested on a session whose activity is `unknown`
- **THEN** the request SHALL be rejected until the activity is resolved
- **AND** the rejection reason SHALL reference the current activity, not a historical end fact

### Requirement: Work results are independent of session activity

TaskRun, AgentJob, Workflow recovery, and retry SHALL continue to judge their own work results independently. They SHALL NOT read AgentSession activity as a work result, and a change in AgentSession activity SHALL NOT cause a TaskRun or AgentJob to enter a terminal state or advance a Workflow.

#### Scenario: A session returns to idle while a task is still being judged

- **WHEN** the session activity returns to `idle` after an execution
- **THEN** the owning TaskRun or AgentJob SHALL continue to judge its own result independently
- **AND** SHALL NOT infer success or failure from the session activity

### Requirement: Activity semantics are uniform across sources and runtimes

OpenCode and Pi, and Workflow-source and Agent-launch-source sessions, SHALL observe identical activity values, transitions, input acceptance, Follow-up, Cancel, and Reset semantics.

#### Scenario: The same transition happens on different runtimes

- **WHEN** an input is accepted on an OpenCode session and on a Pi session under the same conditions
- **THEN** both sessions SHALL transition activity identically
- **AND** both SHALL expose the same Follow-up, Cancel, and Reset eligibility for a given activity
