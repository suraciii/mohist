### Requirement: Runner reconnect reconciles each bound AgentSession

When a Runner reconnects to the control plane, the Server SHALL enumerate every AgentSession bound to that Runner and reconcile each one against the physical Runtime Session on the owning Runner. Reconciliation SHALL use the owning Runner's deterministic physical-Session existence check together with an active-turn snapshot. A reconciliation pass SHALL NOT scan Workflow runs or AgentSessions that are not bound to the reconnecting Runner.

#### Scenario: Reconnect enumerates bound sessions

- **WHEN** a Runner reconnects to the Server
- **THEN** the Server SHALL reconcile each AgentSession whose binding names that Runner
- **AND** SHALL NOT reconcile AgentSessions bound to a different Runner

#### Scenario: Reconciliation checks the unchanged current binding

- **WHEN** the Server reconciles a bound AgentSession on reconnect
- **THEN** it SHALL probe the physical Runtime Session identified by the AgentSession's current binding
- **AND** SHALL capture whether that physical Session has an active turn

### Requirement: A still-existing Session with no active turn keeps its binding and settles to idle

On reconnect reconciliation, when the owning Runner confirms the AgentSession's current physical Runtime Session still exists and has no active turn, the Server SHALL preserve the current binding and SHALL settle the AgentSession activity to `idle`. The settle SHALL apply only when the complete expected binding (Runner, runtime, and Runtime Session id) is still current. The next task SHALL continue through the same binding with the existing context, without replacement or operator repair.

#### Scenario: Still-existing idle session keeps its binding on reconnect

- **WHEN** a Runner reconnects and the owning Runner confirms the AgentSession's bound Runtime Session still exists and has no active turn
- **THEN** the AgentSession SHALL keep its current Runtime Session binding
- **AND** the AgentSession activity SHALL settle to `idle`
- **AND** the next task SHALL continue through the same binding with the existing context

#### Scenario: A still-existing session is not reported missing on reconnect

- **WHEN** a Runner restarts and reconnects, and its previously bound Runtime Session remains queryable on that Runner
- **THEN** the AgentSession SHALL NOT be classified as missing
- **AND** SHALL NOT be replaced with a new empty Runtime Session

### Requirement: A confirmed-missing Session authorizes recovery on reconnect

On reconnect reconciliation, when the owning Runner confirms the AgentSession's current physical Runtime Session is missing, the Server SHALL authorize confirmed-missing recovery for that binding. Recovery SHALL require an `idle` AgentSession and an unchanged expected binding, SHALL create at most one candidate Runtime Session, and SHALL confirm the replacement. A bare reconnect SHALL submit no input — there is no triggering input; when a task or Follow-up input is pending, that task or Follow-up SHALL submit it exactly once against the confirmed replacement, and it SHALL NEVER be replayed by reconnect or retry.

#### Scenario: Confirmed-missing on reconnect triggers one-shot recovery

- **WHEN** a Runner reconnects and its owning check confirms the bound Runtime Session is gone
- **THEN** the Server SHALL authorize confirmed-missing recovery
- **AND** recovery SHALL create at most one candidate Runtime Session and confirm the replacement binding

#### Scenario: Reconnect submits no input; a pending task input is submitted once by the task

- **WHEN** confirmed-missing recovery runs on reconnect
- **THEN** bare reconnect SHALL submit no input against the confirmed replacement
- **AND** a pending task or Follow-up input SHALL be submitted exactly once by that task or Follow-up against the confirmed replacement
- **AND** the input SHALL NOT be replayed by a later reconnect or retry

### Requirement: Transient or unclassifiable results preserve the binding and keep unknown

On reconnect reconciliation, a transient failure, transport failure, unavailable runtime, corrupt response, or otherwise unclassifiable result SHALL preserve the current binding and SHALL leave the AgentSession activity as `unknown`. Such a result SHALL NOT authorize missing-session recovery, SHALL NOT replace the binding, and SHALL NOT replay input. A subsequent reconnect SHALL re-attempt reconciliation.

#### Scenario: Transient failure on reconnect keeps the binding and unknown

- **WHEN** a Runner reconnects and the physical-Session existence check fails with a transient or transport error
- **THEN** the AgentSession SHALL keep its current binding
- **AND** the AgentSession activity SHALL remain `unknown`
- **AND** missing-session recovery SHALL NOT be authorized

#### Scenario: Unavailable runtime on reconnect keeps the binding and unknown

- **WHEN** a Runner reconnects but its runtime is unavailable for the existence check
- **THEN** the AgentSession SHALL keep its current binding
- **AND** the AgentSession activity SHALL remain `unknown`

### Requirement: Reconnect reconciliation rejects superseded-binding facts

A reconciliation fact SHALL apply only to the AgentSession's current Runtime Session binding. A fact probed against a binding that has since been superseded by a Reset, runtime change, or prior recovery SHALL be ignored and SHALL NOT change the current binding, activity, transcript, or accumulated usage.

#### Scenario: A stale-binding reconciliation result is ignored

- **WHEN** reconciliation produces a result for a Runtime Session binding that is no longer current
- **THEN** the Server SHALL ignore the result
- **AND** the current binding, activity, transcript, and accumulated usage SHALL remain unchanged
