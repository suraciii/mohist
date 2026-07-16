### Requirement: Recovery declaration is immutable

A workflow task's `recovery` declaration SHALL remain immutable definition data throughout its lifecycle. The declared `budget`, ordered `handlers`, each handler's `when`, `tasks`, and `retrySelf` values SHALL remain structurally equivalent to the workflow declaration on every attempt. Budget consumption MUST be represented only by a separate per-attempt `recoveryRemaining` value; it MUST NOT lower `recovery.budget` or otherwise modify the declaration. Every persisted task attempt and every event, timeline, work-item, dispatch, or follow-up representation that carries recovery data SHALL carry the unchanged declaration.

#### Scenario: Automatic self-retry preserves the declaration

- **WHEN** a task declared with recovery budget 2 and a matching handler schedules an automatic self-retry from an attempt whose `recoveryRemaining` is 2
- **THEN** the generated self-retry SHALL carry recovery budget 2 and the same ordered handlers as the workflow declaration
- **AND** the generated self-retry SHALL carry `recoveryRemaining` 1 separately from that declaration

#### Scenario: Exhaustion does not rewrite recorded recovery data

- **WHEN** a task consumes all automatic recoveries in a round
- **THEN** every persisted attempt and every event or timeline representation that carries its recovery declaration SHALL still contain the originally declared budget and handlers
- **AND** no representation SHALL encode the consumed allowance by replacing the declared budget with a smaller value

### Requirement: Automatic recovery budget is bounded per round

Each task recovery round SHALL have a remaining allowance initialized from the task's declared recovery budget. Whenever a matching handler is selected while `recoveryRemaining` is greater than zero, scheduling that handler's automatic recovery SHALL consume exactly one allowance. An automatic self-retry SHALL continue the same round with the decremented allowance. When `recoveryRemaining` is zero, the system MUST NOT schedule handler tasks or an automatic self-retry and SHALL preserve the task's ordinary normalized result. A single continuous round MUST NOT schedule more automatic recoveries than the declared budget.

#### Scenario: Budget two permits exactly two automatic recoveries

- **WHEN** a task with declared budget 2 produces matching output on consecutive attempts whose `recoveryRemaining` values are 2, 1, and 0
- **THEN** the attempts with remaining values 2 and 1 SHALL each schedule one recovery sequence
- **AND** their generated self-retries SHALL carry remaining values 1 and 0 respectively
- **AND** the attempt with remaining value 0 SHALL schedule no handler task and no automatic self-retry
- **AND** the round SHALL have scheduled exactly two automatic recoveries

#### Scenario: Zero budget preserves the ordinary task result

- **WHEN** a recovery-enabled task has `recoveryRemaining` 0 and its action output matches a handler
- **THEN** the system SHALL schedule no automatic recovery follow-up
- **AND** the task SHALL retain the completed or failed result produced by normal result handling

### Requirement: Recovery selection and follow-up construction remain stable

Recovery matching SHALL evaluate handlers in declaration order against action output using the declared `field=value` expression and SHALL select only the first matching handler. Matching SHALL remain independent of whether the action's normalized result is completed or failed. When a handler matches and allowance remains, the system SHALL schedule that handler's declared tasks in declaration order and SHALL append the original task's automatic self-retry after those tasks when `retrySelf` is true. The self-retry SHALL preserve the original task's definition fields and immutable recovery declaration; only its attempt identity and per-round remaining allowance SHALL advance.

#### Scenario: First matching handler wins and self-retry runs last

- **WHEN** a task with positive `recoveryRemaining` produces output that matches more than one declared handler and the first matching handler has two tasks with `retrySelf` enabled
- **THEN** the system SHALL schedule only the first matching handler's two tasks in their declared order
- **AND** it SHALL append the task's automatic self-retry after both handler tasks
- **AND** it SHALL NOT schedule tasks from any later matching handler

#### Scenario: Matching completed output still triggers recovery

- **WHEN** a task has positive `recoveryRemaining`, its normalized result is completed, and its output matches a recovery handler
- **THEN** the system SHALL schedule the matching handler's recovery follow-ups

#### Scenario: Unmatched output does not consume budget

- **WHEN** a task's output matches no recovery handler
- **THEN** the system SHALL schedule no recovery follow-up
- **AND** it SHALL preserve the task's ordinary normalized result
- **AND** it SHALL NOT consume the task's `recoveryRemaining` allowance

### Requirement: Manual retry starts a full-budget recovery round

A user-initiated retry of a failed recovery-enabled task SHALL create a new task attempt from the task's immutable definition data and SHALL start a new recovery round. The new attempt's `recoveryRemaining` SHALL equal the declared recovery budget regardless of the failed attempt's remaining allowance. Manual retry MUST NOT inherit an exhausted or partially consumed `recoveryRemaining` value from the preceding round, and it SHALL NOT alter prior attempts or their recorded recovery declarations.

#### Scenario: Manual retry restores an exhausted budget

- **WHEN** a task declared with recovery budget 2 exhausts its automatic recovery round, fails with `recoveryRemaining` 0, and the user invokes `mo issue retry`
- **THEN** the new task attempt SHALL carry the unchanged recovery declaration with budget 2
- **AND** the new attempt SHALL start with `recoveryRemaining` 2
- **AND** matching output from that attempt SHALL again schedule the declared recovery tasks and automatic self-retry

#### Scenario: The new manual round remains bounded by the declaration

- **WHEN** the manually retried task and its automatic self-retries continue to produce matching output
- **THEN** the new round SHALL schedule at most the declared number of automatic recoveries
- **AND** after those recoveries are consumed, the next matching attempt SHALL produce no automatic recovery follow-up and SHALL retain its ordinary failure

#### Scenario: Manual retry preserves previous attempt history

- **WHEN** the user manually retries a task after its recovery round has failed
- **THEN** the failed attempts from the preceding round SHALL remain recorded with their original identities, outcomes, and unchanged recovery declarations
- **AND** the manually retried task SHALL be recorded as a new attempt with a fresh full-budget allowance
