### Requirement: MaxConcurrentRuns is a real-time scheduling gate, not part of the execution definition

`MaxConcurrentRuns` SHALL be enforced as a real-time scheduling gate that bounds the number of the Agent's concurrently active executions across all of its sessions. An unset limit SHALL mean no per-agent limit (the Agent is gated only by runner capacity, plus per-session serial execution). The limit SHALL NOT be part of the Agent execution definition snapshot or the AgentSession; changing it SHALL NOT alter any session's captured execution definition or Runtime binding.

#### Scenario: Unset limit imposes no per-agent bound
- **WHEN** an Agent has no MaxConcurrentRuns set
- **THEN** its executions SHALL be bounded only by runner capacity, with no per-agent serialization beyond per-session serial execution

#### Scenario: Editing the limit does not change captured definitions
- **WHEN** MaxConcurrentRuns is changed after a launch
- **THEN** no existing AgentSession's execution definition or Runtime binding SHALL be rewritten

### Requirement: The gate applies consistently to every entry point

The concurrency gate SHALL apply to every entry point that starts or continues an execution — Web launch, CLI launch, event routing, comment mention, and follow-up — so that no entry point can bypass the Agent's current MaxConcurrentRuns. Both launches and follow-ups that would start a new execution are subject to the bound. In this change a launch that would exceed the bound waits; a follow-up that would exceed the bound is rejected with a distinct retryable reason rather than queued (full follow-up queuing is a separate change).

#### Scenario: Follow-up honors the gate
- **WHEN** a follow-up that would start a new execution is submitted to an AgentSession whose Agent has reached its MaxConcurrentRuns limit
- **THEN** the follow-up SHALL be subject to the gate and SHALL NOT bypass it merely because it continues an existing session; in this change it is rejected with a distinct retryable reason (not queued) so the caller retries with the same identity, while a follow-up to a busy session is unaffected by per-session serial execution

#### Scenario: Every launch entry point honors the gate
- **WHEN** a launch is submitted through any entry point for an Agent at its MaxConcurrentRuns limit
- **THEN** the work SHALL wait rather than start a new execution that exceeds the limit

### Requirement: Reaching the limit causes waiting, not failure

When an Agent's active executions reach its MaxConcurrentRuns, newly submitted work for that Agent SHALL enter a waiting state and proceed when an active execution frees capacity. Reaching the concurrency limit SHALL NOT cause terminal failure.

#### Scenario: New work waits at the limit
- **WHEN** an Agent with MaxConcurrentRuns set to N has N active executions and new work is submitted
- **THEN** the new work SHALL wait and SHALL NOT enter a terminal `Failed` state due to the concurrency limit

#### Scenario: Waiting work proceeds when a slot frees
- **WHEN** an active execution for an Agent at its limit completes and a waiting work item exists
- **THEN** the waiting work SHALL proceed to execution without being resubmitted by the user

### Requirement: Lowering the limit does not stop active work

Lowering MaxConcurrentRuns SHALL NOT stop or interrupt executions that are already active, and SHALL NOT rewrite or restart any existing AgentSession. The new, lower limit SHALL apply only to work submitted after the change.

#### Scenario: Active work survives a lower limit
- **WHEN** MaxConcurrentRuns is lowered while executions are active
- **THEN** the active executions SHALL continue uninterrupted, and only subsequently submitted work SHALL be gated by the new lower limit

#### Scenario: Lowering does not touch existing sessions
- **WHEN** MaxConcurrentRuns is lowered
- **THEN** no existing AgentSession SHALL be rewritten, restarted, or have its execution definition changed

### Requirement: Raising the limit lets waiting work proceed

Raising MaxConcurrentRuns SHALL let work that was waiting under the previous lower limit proceed under the new limit, without the user resubmitting it.

#### Scenario: Waiting work proceeds after a raise
- **WHEN** MaxConcurrentRuns is raised and work is waiting under the previous lower limit
- **THEN** the waiting work SHALL proceed to execution under the new higher limit without resubmission
