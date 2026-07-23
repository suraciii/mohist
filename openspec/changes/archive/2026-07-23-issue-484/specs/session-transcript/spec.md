### Requirement: The transcript is a flat append-only session record

The AgentSession transcript SHALL be a single, flat, ordered record appended to over the lifetime of one AgentSession. Each accepted input SHALL be recorded as an ordinary input boundary in that transcript, not as a distinct domain resource with its own identity or lifecycle. The transcript SHALL NOT create per-execution sub-resources, execution IDs, or message histories grouped by execution, Runtime Session, or attempt.

#### Scenario: Two inputs land on the same session

- **WHEN** a second accepted input follows a first on the same AgentSession
- **THEN** both SHALL appear as ordered input boundaries in the same flat transcript
- **AND** neither SHALL introduce a separate execution entity or execution identifier

### Requirement: Each accepted input is one session.input boundary

Each input accepted by Mohist SHALL be recorded as exactly one `session.input` transcript event carrying the input text, its source, and the acceptance time. Recording `session.input` and the `idle` to `active` activity transition SHALL be atomic: the input is durable as a transcript fact only when the activity transition is also durable.

#### Scenario: An input is accepted while idle

- **WHEN** the session accepts an input while `idle`
- **THEN** exactly one `session.input` event SHALL be appended to the transcript
- **AND** the `idle` to `active` transition SHALL be persisted atomically with it

### Requirement: Activity transitions are recorded as session.activity events

A change in AgentSession activity SHALL be recorded as a `session.activity` transcript event carrying the new activity value and the observation time. Activity events SHALL express continuous session state; they SHALL NOT repeat or stand in for TaskRun or AgentJob work results.

#### Scenario: Execution stops and the session returns to idle

- **WHEN** an execution is confirmed stopped and the activity returns to `idle`
- **THEN** a `session.activity` event with activity `idle` SHALL be recorded
- **AND** it SHALL NOT carry the TaskRun or AgentJob result

### Requirement: Binding replacement is recorded as a context reset fact

When an existing binding is replaced (by Reset, runtime change, or confirmed-missing recovery), the transcript SHALL record exactly one `session.context_reset` event. The event SHALL carry only the reason (`reset`, `runtime-change`, or `missing-recovery`) and the observation time. It SHALL NOT carry the old or new physical Runtime Session identifier, and SHALL NOT establish or reference a binding history. The `session.context_reset` event and the binding replacement SHALL be persisted atomically, and the event SHALL appear before the next `session.input` that runs against the new binding.

#### Scenario: A reset replaces the binding

- **WHEN** a Reset replaces the current binding while the session is `idle`
- **THEN** a `session.context_reset` event with reason `reset` SHALL be recorded atomically with the replacement
- **AND** the event payload SHALL NOT contain the previous or new Runtime Session identifier

#### Scenario: A confirmed-missing recovery replaces the binding

- **WHEN** confirmed-missing recovery replaces the binding before a new input
- **THEN** a `session.context_reset` event with reason `missing-recovery` SHALL be recorded atomically with the replacement
- **AND** it SHALL appear before the `session.input` event for the recovered input

#### Scenario: The first binding is established from no binding

- **WHEN** the first physical Runtime Session is established for a session that had no prior binding
- **THEN** no `session.context_reset` event SHALL be recorded

### Requirement: Legacy terminal and follow-up outcome events are removed

The transcript SHALL NOT contain `session.closed`, `session.followup_completed`, or `session.followup_failed` events. Session state, activity, command eligibility, and UI status SHALL NOT be derived from these event types. The Follow-up acceptance outcome SHALL be expressed through the `session.input` boundary and subsequent activity transitions, and a Follow-up whose prompt is rejected SHALL be expressed through the activity transition without a dedicated follow-up failure event.

#### Scenario: A follow-up prompt is rejected

- **WHEN** a Follow-up prompt is rejected by the Runtime without producing a turn
- **THEN** the transcript SHALL NOT record a `session.followup_failed` event
- **AND** the rejection SHALL be expressed through the activity transition and runtime diagnostics only

#### Scenario: An execution ends

- **WHEN** an execution ends on the session
- **THEN** the transcript SHALL NOT record a `session.closed` event
- **AND** the session SHALL remain open with its activity returned to `idle`
