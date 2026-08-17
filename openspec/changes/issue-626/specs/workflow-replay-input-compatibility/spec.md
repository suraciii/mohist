### Requirement: Historical resourceProfile is ignored during core/script replay
A Workflow execution MUST treat `resourceProfile` as a retired compatibility field only when a persisted `core/script` task declaration is replayed or recovered. Before current Action input validation and handler invocation, the system MUST exclude `resourceProfile` from the effective Action input. Its stored value MUST NOT affect the script command, shell, timeout, workspace, resource limits, or any other execution behavior.

#### Scenario: Direct redelivery of an older core/script task
- **WHEN** a persisted Workflow task attempt using `core/script` is directly redelivered with valid `run`, `shell`, and `timeout` inputs plus a historical `resourceProfile` field
- **THEN** the Runner MUST accept and execute the task using the current `core/script` behavior, with `resourceProfile` absent from the Action input and without restoring per-work resource containment

#### Scenario: Recovery continuation carries the retired field
- **WHEN** a historical `core/script` task containing `resourceProfile` enters a configured `retrySelf` recovery path
- **THEN** the generated continuation MUST pass current Action validation and execute without treating `resourceProfile` as a supported input or applying its stored value

### Requirement: Replay and recovery preserve the current task contract
Workflow replay and recovery MUST preserve the supported `core/script` inputs `run`, `shell`, and `timeout`, including their values and unresolved templates, together with the task identity and metadata, completion expectations, recovery declaration, and remaining recovery budget. Compatibility handling MUST discard only the retired `resourceProfile` field from effective Action execution and MUST NOT rewrite the historical Workflow state in place.

#### Scenario: Redelivery preserves supported declarations and task metadata
- **WHEN** a persisted `core/script` task with `resourceProfile` is redelivered
- **THEN** the redelivered work MUST retain its work and task-attempt identity, title, Action selection, supported input values and templates, artifact and variable declarations, completion expectations, recovery declaration, and recovery budget while ignoring only `resourceProfile` for Action execution

#### Scenario: Self-retry preserves completion and recovery semantics
- **WHEN** a redelivered historical `core/script` task with a positive remaining recovery budget fails and its matching handler declares `retrySelf`
- **THEN** the continuation MUST retain the original task metadata, supported input values and templates, completion expectations, artifacts, variable declarations, and recovery declaration, and MUST decrement the remaining recovery budget by exactly one without reintroducing `resourceProfile` as an effective input

### Requirement: The current core/script contract remains strict
The current Action catalog MUST continue to declare `core/script` with required `run` and optional `shell` and `timeout` inputs, and MUST NOT declare `resourceProfile` as an input or execution capability. New Workflow definitions MUST continue to reject `resourceProfile`, and compatibility for historical replay or recovery MUST NOT relax rejection of any other unknown input or invalid supported input.

#### Scenario: A new definition cannot use resourceProfile
- **WHEN** a newly parsed Workflow definition declares `resourceProfile` under a `core/script` task
- **THEN** current Workflow definition validation MUST reject the task as declaring an unknown input and MUST NOT make `resourceProfile` available for execution

#### Scenario: An unrelated unknown input remains rejected during replay
- **WHEN** a persisted or recovery-generated `core/script` task contains `resourceProfile` and any other undeclared input
- **THEN** Action validation MUST reject the task for the unrelated input and MUST NOT execute it or silently discard that input

#### Scenario: Supported input validation remains enforced
- **WHEN** a historical `core/script` replay supplies `run`, `shell`, or `timeout` with a value that violates the current input contract
- **THEN** Action validation MUST reject the invalid supported input using the current validation behavior, regardless of whether `resourceProfile` is also present
