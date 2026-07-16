### Requirement: Recovery declaration is immutable

A workflow task's `recovery` declaration SHALL remain immutable definition data throughout its lifecycle. The declared `budget`, ordered `handlers`, each handler's `when`, `tasks`, and `retrySelf` values SHALL remain structurally equivalent to the workflow declaration on every attempt. Budget consumption MUST be represented only by a separate per-attempt `recoveryRemaining` value; it MUST NOT lower `recovery.budget` or otherwise modify the declaration. Every persisted task attempt and every event, timeline, work-item, dispatch, or follow-up representation that carries recovery data SHALL carry the unchanged declaration.

#### Scenario: Automatic self-retry preserves the declaration

- **WHEN** a task declared with recovery budget 2 and a matching handler schedules an automatic self-retry from an attempt whose effective remaining allowance is 2
- **THEN** the generated self-retry SHALL carry recovery budget 2 and the same ordered handlers as the workflow declaration
- **AND** the generated self-retry SHALL carry `recoveryRemaining` 1 separately from that declaration

#### Scenario: Exhaustion does not rewrite recorded recovery data

- **WHEN** a task consumes all automatic recoveries in a round
- **THEN** every persisted attempt and every event or timeline representation that carries its recovery declaration SHALL still contain the originally declared budget and handlers
- **AND** no representation SHALL encode the consumed allowance by replacing the declared budget with a smaller value

### Requirement: Recovery allowance has one mutation authority

Fresh recovery-enabled task construction SHALL carry an explicit `null` `recoveryRemaining` marker. The runner recovery evaluator SHALL be the only authority that interprets this marker as the declared budget and authors numeric remaining values; workflow persistence, work-item construction, dispatch translation, report translation, and runtime task insertion SHALL preserve explicit `null` or numeric state unchanged. Every runner-produced follow-up task that carries a recovery declaration MUST carry an explicit numeric `recoveryRemaining`: a handler task starting its own round carries its declared budget, while an automatic self-retry carries the decremented value. An absent property MUST NOT be interpreted as fresh, and malformed numeric values MUST NOT increase the effective allowance beyond the declared budget.

#### Scenario: Fresh marker is initialized by the runner

- **WHEN** a fresh recovery-enabled task with declared budget 2 is dispatched with explicit `recoveryRemaining: null`
- **THEN** the runner SHALL evaluate that attempt with an effective remaining allowance of 2
- **AND** the control plane SHALL NOT replace the fresh marker with a numeric allowance before dispatch

#### Scenario: Remaining allowance passes through unchanged

- **WHEN** the runner produces an automatic self-retry with recovery budget 2 and `recoveryRemaining` 1
- **THEN** every control-plane persistence and translation boundary SHALL preserve the value 1
- **AND** the next dispatch of that self-retry SHALL carry recovery budget 2 and `recoveryRemaining` 1

#### Scenario: Missing continuation state fails closed

- **WHEN** a runner-produced recovery-enabled follow-up omits the `recoveryRemaining` property rather than carrying a number
- **THEN** the system SHALL reject the malformed follow-up or make it ineligible for automatic recovery
- **AND** it MUST NOT initialize the follow-up to the full declared budget

#### Scenario: Malformed allowance cannot expand a round

- **WHEN** recovery evaluation receives a negative `recoveryRemaining` or a value greater than the declared budget
- **THEN** a negative value SHALL be bounded to 0 and permit no automatic recovery
- **AND** an above-budget value SHALL be bounded to the declared budget
- **AND** the round MUST NOT schedule more automatic recoveries than the declaration permits

### Requirement: Persisted legacy recovery state is normalized

When persisted task attempts omit the `recoveryRemaining` property because they predate it and encode consumption by lowering `recovery.budget`, the system SHALL normalize them before deserialization and further execution. For attempts sharing a definition id whose recovery declarations are structurally equivalent except for budget, the earliest attempt's recovery declaration SHALL be canonical, while each attempt's previously stored budget SHALL become that attempt's numeric `recoveryRemaining`; task identities, outcomes, ordering, and other history SHALL remain unchanged. An explicitly present `recoveryRemaining`, including `null` or 0, SHALL identify new-format state and MUST NOT be normalized again. A same-definition-id group whose non-budget recovery structure differs SHALL be rejected as ambiguous rather than rewritten. This normalization SHALL allow manual retry of an already-exhausted pre-change attempt to start from the original declared budget.

#### Scenario: Legacy attempts recover immutable declaration and separate state

- **WHEN** persisted attempts of one task have no `recoveryRemaining` and carry otherwise-equivalent recovery declarations with budgets 2, 1, and 0
- **THEN** normalization SHALL preserve their attempt identities and outcomes
- **AND** all three attempts SHALL carry the canonical recovery declaration with budget 2
- **AND** their `recoveryRemaining` values SHALL be 2, 1, and 0 respectively

#### Scenario: New-format state is not normalized again

- **WHEN** persisted attempts carry an explicitly present `recoveryRemaining` property with `null`, 1, or 0
- **THEN** legacy normalization SHALL leave their recovery declaration and remaining state unchanged
- **AND** repeated loading SHALL be idempotent

#### Scenario: Ambiguous reused definition id fails safely

- **WHEN** attempts sharing a definition id omit `recoveryRemaining` but their handlers, predicates, tasks, or `retrySelf` values differ
- **THEN** the system SHALL reject legacy normalization for that group with an actionable error
- **AND** it SHALL NOT replace either declaration with the other

#### Scenario: Manual retry works for a pre-change exhausted attempt

- **WHEN** a pre-change failed attempt encoded exhaustion as recovery budget 0 and an earlier attempt of the same definition retained the original budget 2 declaration
- **THEN** normalization SHALL restore the failed attempt's declaration to budget 2 and preserve `recoveryRemaining` 0
- **AND** a subsequent user-initiated retry SHALL create a fresh attempt with recovery budget 2 and explicit `recoveryRemaining: null`, which the runner SHALL evaluate with allowance 2

### Requirement: Automatic recovery budget is bounded per round

Each task recovery round SHALL have a remaining allowance initialized from the task's declared recovery budget. Whenever a matching handler is selected while `recoveryRemaining` is greater than zero, scheduling that handler's automatic recovery SHALL consume exactly one allowance. An automatic self-retry SHALL continue the same round with the decremented allowance. When `recoveryRemaining` is zero, the system MUST NOT schedule handler tasks or an automatic self-retry and SHALL preserve the task's ordinary normalized result. A single continuous round MUST NOT schedule more automatic recoveries than the declared budget.

#### Scenario: Budget two permits exactly two automatic recoveries

- **WHEN** a task with declared budget 2 produces matching output on consecutive attempts whose effective remaining allowances are 2, 1, and 0
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

#### Scenario: Handler without retrySelf does not append the original task

- **WHEN** a task with positive `recoveryRemaining` matches a handler whose `retrySelf` is false
- **THEN** the system SHALL schedule only that handler's declared tasks in declaration order
- **AND** it SHALL NOT append an automatic self-retry of the original task

#### Scenario: Matching completed output still triggers recovery

- **WHEN** a task has positive `recoveryRemaining`, its normalized result is completed, and its output matches a recovery handler
- **THEN** the system SHALL schedule the matching handler's recovery follow-ups

#### Scenario: Unmatched output does not consume budget

- **WHEN** a task's output matches no recovery handler
- **THEN** the system SHALL schedule no recovery follow-up
- **AND** it SHALL preserve the task's ordinary normalized result
- **AND** it SHALL NOT consume the task's `recoveryRemaining` allowance

### Requirement: Manual retry starts a full-budget recovery round

A user-initiated retry of a failed recovery-enabled task SHALL create a new task attempt from the task's immutable definition data and SHALL start a new recovery round. The new attempt SHALL carry explicit `recoveryRemaining: null`, which the runner SHALL interpret as an effective allowance equal to the declared recovery budget regardless of the failed attempt's remaining allowance. Manual retry MUST NOT inherit an exhausted or partially consumed numeric value from the preceding round, and it SHALL NOT alter prior attempts or their recorded recovery declarations.

#### Scenario: Manual retry restores an exhausted budget

- **WHEN** a task declared with recovery budget 2 exhausts its automatic recovery round, fails with `recoveryRemaining` 0, and the user invokes `mo issue retry`
- **THEN** the new task attempt SHALL carry the unchanged recovery declaration with budget 2
- **AND** the new attempt SHALL carry explicit `recoveryRemaining: null` and the runner SHALL evaluate it with allowance 2
- **AND** matching output from that attempt SHALL again schedule the declared recovery tasks and automatic self-retry

#### Scenario: The new manual round remains bounded by the declaration

- **WHEN** the manually retried task and its automatic self-retries continue to produce matching output
- **THEN** the new round SHALL schedule at most the declared number of automatic recoveries
- **AND** after those recoveries are consumed, the next matching attempt SHALL produce no automatic recovery follow-up and SHALL retain its ordinary failure

#### Scenario: Manual retry preserves previous attempt history

- **WHEN** the user manually retries a task after its recovery round has failed
- **THEN** the failed attempts from the preceding round SHALL remain recorded with their original identities, outcomes, and unchanged recovery declarations
- **AND** the manually retried task SHALL be recorded as a new attempt with a fresh full-budget allowance
