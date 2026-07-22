### Requirement: The session stores exactly one current binding

An AgentSession SHALL store at most one current runtime binding, consisting of the current Runner, runtime, and Runtime Session identifier. The session SHALL NOT store, expose, or query a history or lineage of prior physical Runtime Sessions. A binding replacement SHALL NOT change the AgentSession identifier, source, working directory, transcript, or cumulative usage; only the current binding and the Runtime context change, and the Runtime context SHALL start empty after a replacement.

#### Scenario: A binding is replaced

- **WHEN** the current binding is replaced by Reset, runtime change, or recovery
- **THEN** the AgentSession identifier, source, working directory, transcript, and cumulative usage SHALL be unchanged
- **AND** the Runtime context SHALL start from empty
- **AND** no prior binding list SHALL be persisted or exposed

### Requirement: Binding replacement is compare-and-swap and idle-only

A binding replacement SHALL require the session activity to be `idle`, and SHALL compare the full expected current binding against the actual current binding before replacing. If the expected binding no longer matches the current binding, the replacement SHALL be rejected and the current binding SHALL be preserved. A Runtime event SHALL carry the Runtime Session identifier that produced it, and the session SHALL reject any event whose identifier does not match the current binding.

#### Scenario: The expected binding is stale

- **WHEN** a replacement is attempted with an expected binding that no longer matches the current binding
- **THEN** the replacement SHALL be rejected
- **AND** the current binding SHALL remain unchanged

#### Scenario: A late event arrives from a superseded Runtime Session

- **WHEN** a Runtime event arrives carrying a Runtime Session identifier that is not the current binding
- **THEN** the event SHALL be rejected
- **AND** the current binding, activity, and transcript SHALL remain unchanged

#### Scenario: A replacement is attempted while not idle

- **WHEN** a binding replacement is attempted while the activity is `active` or `unknown`
- **THEN** the replacement SHALL be rejected

### Requirement: Confirmed-missing recovery creates at most one empty session before a new input

Before submitting a new independent input (a Workflow task, an AgentJob, or an idle Follow-up), if the owning Runner's runtime adapter deterministically confirms that the current Runtime Session no longer exists, Mohist SHALL create at most one empty Runtime Session on the same Runner and runtime for the session's working directory, replace the binding via compare-and-swap, and then accept and submit the input. Recovery SHALL submit the input exactly once, only after the server has confirmed the new binding is current, and SHALL NOT consume a Workflow recovery budget or create a new work attempt.

#### Scenario: The runtime confirms the session is missing before a task input

- **WHEN** a Workflow task input is about to be submitted and the owning Runner confirms the current Runtime Session no longer exists
- **AND** the session activity is `idle`
- **THEN** Mohist SHALL create one empty Runtime Session on the same Runner and runtime
- **AND** SHALL replace the binding via compare-and-swap
- **AND** SHALL submit the input exactly once after the binding is confirmed current

#### Scenario: The runtime confirms the session is missing before an idle follow-up

- **WHEN** an idle Follow-up input is about to be submitted and the owning Runner confirms the current Runtime Session no longer exists
- **THEN** Mohist SHALL apply the same recovery as for a task input
- **AND** SHALL submit the follow-up input exactly once

#### Scenario: Recovery does not retry the work

- **WHEN** confirmed-missing recovery succeeds and the input is submitted
- **THEN** the owning TaskRun or AgentJob SHALL continue on the same work attempt
- **AND** SHALL NOT consume a Workflow recovery budget or start a new attempt

### Requirement: Non-recovery conditions preserve the binding and refuse replay

Recovery SHALL NOT trigger, and the current binding SHALL be preserved, when any of the following hold: the activity is `active` or `unknown`; the input may already have been submitted or its acceptance is unknown; the runtime is temporarily unavailable; a request times out; a permission failure occurs; a server-side error occurs; data is corrupt; the response cannot be classified as present or missing; or the request lands on a Runner other than the binding's owning Runner. In every non-recovery case the operation SHALL fail explicitly and SHALL NOT automatically replay the input.

#### Scenario: The runtime is unavailable or times out

- **WHEN** the owning Runner cannot be reached, the request times out, or the runtime reports a transient error
- **THEN** the current binding SHALL be preserved
- **AND** the input SHALL NOT be submitted or replayed
- **AND** the operation SHALL fail explicitly

#### Scenario: The result cannot be classified

- **WHEN** the runtime returns a response that cannot be classified as present or missing, or the data is corrupt
- **THEN** the current binding SHALL be preserved
- **AND** recovery SHALL NOT trigger

#### Scenario: The request lands on a different Runner

- **WHEN** a request to check the binding resolves to a Runner other than the binding's owning Runner
- **THEN** that Runner's local result SHALL NOT be used to infer the original session is missing
- **AND** recovery SHALL NOT trigger
- **AND** the request SHALL route back to the owning Runner or fail explicitly

#### Scenario: The session is executing or uncertain

- **WHEN** the activity is `active` or `unknown`, or the input acceptance is unknown
- **THEN** recovery SHALL NOT trigger
- **AND** the current binding SHALL be preserved

### Requirement: Reset and runtime change use the same binding-replacement path

A user Reset and an explicit runtime change SHALL replace the binding using the same idle-only compare-and-swap path as confirmed-missing recovery. Reset SHALL create a new empty Runtime Session without changing the runtime; a runtime change MAY change the runtime and Runner but SHALL NOT change the session's working directory. A Reset SHALL proceed even if the original Runtime Session is also missing, because Reset expresses a user's intent to discard Runtime context. Neither operation SHALL migrate, copy, or replay Runtime context into the new session.

#### Scenario: A reset is requested while idle

- **WHEN** a Reset is requested while the activity is `idle`
- **THEN** a new empty Runtime Session SHALL be created and bound via compare-and-swap
- **AND** the session working directory, transcript, and cumulative usage SHALL be preserved

#### Scenario: Compact and cancel do not trigger recovery

- **WHEN** a Compact or Cancel is requested on a session whose Runtime Session is missing
- **THEN** the operation SHALL NOT create a new Runtime Session
- **AND** SHALL act on the current binding or fail explicitly

### Requirement: A created-but-unbound candidate does not submit input

If a candidate Runtime Session is created but the binding replacement is not confirmed as current (for example, because the expected binding changed concurrently or persistence failed), the input SHALL NOT be submitted. An unbound candidate SHALL produce only a diagnostic; it SHALL NOT introduce a compensation protocol or cause physical session data to be copied.

#### Scenario: The binding changes during recovery

- **WHEN** a candidate session is created but the expected binding no longer matches the current binding at replacement time
- **THEN** the input SHALL NOT be submitted
- **AND** the candidate SHALL not affect the current binding or transcript beyond a diagnostic
