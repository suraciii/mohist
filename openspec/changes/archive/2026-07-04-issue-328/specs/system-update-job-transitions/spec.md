### Requirement: A single shared transition helper owns the build-next-state-and-save template

The repeated "construct the next job state → append a log entry → persist → optionally release the lock" template SHALL be consolidated into one shared transition helper (or a small cohesive set of helpers), rather than being re-implemented at every transition site. Every place that builds a next state and persists it — the build/restart/waiting pipeline steps, the readiness-driven waiting and ready transitions, the supersession transitions, the recovered transition, and the CLI-outcome recording — SHALL route through this shared helper instead of hand-rolling the equivalent `state with { ... }` + `AppendLog` + `SaveAsync` sequence.

#### Scenario: Transition sites route through the shared helper

- **WHEN** any code path advances a job to a new status/stage and persists it
- **THEN** the persist SHALL be performed by the shared transition helper
- **AND** the log-entry append SHALL be performed by the shared helper rather than inline `AppendLog` + `state with` at each call site

#### Scenario: Lock release is a parameter of the shared helper

- **WHEN** a transition reaches a terminal status that requires releasing the file lock
- **THEN** the lock release SHALL be expressed as an option of the shared helper
- **AND** call sites SHALL NOT each re-implement the "save then release lock" sequence

### Requirement: Failure is recorded through exactly one failure handler

Every `catch` block and every non-zero-exit branch that records a failed job state SHALL reuse the existing failure handler (the `FailAsync` method or its shared-helper successor) instead of reconstructing a failed `SystemUpdateJobState` inline. There SHALL be exactly one definition of how a job transitions to the `failed` status: which fields are set, how the reason and log entry are composed, and how the state is persisted. No call site SHALL hand-write an equivalent failed-state construction.

#### Scenario: Catch blocks delegate to the failure handler

- **WHEN** an exception is caught in the update pipeline or the start path
- **THEN** the resulting `failed` state SHALL be produced by invoking the failure handler
- **AND** the catch block SHALL NOT independently construct a `state with { Status = "failed", ... }` object

#### Scenario: Non-zero command exit records failure via the handler

- **WHEN** a dispatched command returns a non-zero exit code
- **THEN** the failed transition SHALL be recorded by the failure handler
- **AND** the handler SHALL compose the reason from the command output and append the stage-appropriate log entry

#### Scenario: Runner-restore failure reuses the failure definition

- **WHEN** a runner restore attempt fails after an earlier update failure
- **THEN** the resulting `failed` (or `recovered`) state SHALL be assembled through the shared transition helpers
- **AND** the failure-state shape (status, outcome, `UnavailableCapability`, log message) SHALL come from one definition rather than an inline duplicate

### Requirement: The consolidated transitions preserve existing semantics

Consolidating the transition and failure templates into shared helpers SHALL be behavior-preserving. The persisted status values, stage labels, reason strings, log-entry stages and messages, `CompletedAt`/`UpdatedAt` timestamps, lock-release points, and the 200-entry log bound SHALL remain identical to before this change for every transition. The shared helper is a relocation of duplicated logic, not a semantic change.

#### Scenario: Failed-state log content is unchanged after consolidation

- **WHEN** a job fails (whether via exception, non-zero exit, or restore failure)
- **THEN** the persisted `failed` state SHALL carry the same reason string, stage label, and log-entry message it would have carried before consolidation
- **AND** `CompletedAt` and `UpdatedAt` SHALL be set to the failure timestamp as before

#### Scenario: Recovered-state shape is unchanged after consolidation

- **WHEN** a runner restore succeeds after an update failure
- **THEN** the persisted state SHALL be `recovered` with outcome `recovered`, a `Recovered` log entry, and a null `UnavailableCapability` exactly as before consolidation

#### Scenario: Log bounding is applied uniformly

- **WHEN** any transition appends a log entry through the shared helper
- **THEN** the persisted log list SHALL be capped at the 200-entry maximum (most-recent retained)
- **AND** the cap logic SHALL be defined once in the shared helper rather than per call site

### Requirement: No duplicated transition logic remains

After consolidation, an audit of the orchestrator SHALL find exactly one implementation of each distinct transition template: one "advance to next status and persist" path, one "mark failed and persist" path, and one log-append/bound rule. Stale hand-written copies (the inline `state with { Status = "failed", ... }` blocks and inline append-then-save sequences previously scattered across the file) SHALL be removed so they cannot drift from the shared definition.

#### Scenario: No inline failed-state construction remains

- **WHEN** the orchestrator source is searched for inline failed-state construction
- **THEN** no catch block or exit-code branch SHALL contain a `state with { Status = "failed"` expression outside the shared failure handler

#### Scenario: No inline append-and-save sequence remains

- **WHEN** the orchestrator source is searched for duplicated build-next-state-and-save sequences
- **THEN** transition sites SHALL invoke the shared helper
- **AND** the inline `AppendLog(...)` + `await _store.SaveAsync(...)` pattern SHALL appear only inside the shared helper
