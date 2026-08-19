### Requirement: Run initialization captures the immutable workflow definition
The Server SHALL serialize the complete effective `WorkflowDefinition` at `BindWorkflowRun` time and persist it as the run's write-once `BoundWorkflowDefinitionJson`. The snapshot SHALL include every stage's task, check, approval, lock, and resource data plus top-level approval and recovery data, including command, timeout, and recovery fields. Later stage initialization and stage-lock resolution SHALL read this snapshot rather than the current profile provider. Existing state without this field SHALL remain readable as explicit legacy mode and SHALL use the retained pre-change aggregate definition for affected built-in profiles without rewriting historical task attempts.

#### Scenario: A legacy run keeps its definition after profile activation
- **WHEN** a run is bound while the affected built-in profile still contains aggregate `verify`
- **AND** the profile is replaced with the six-lane definition before the run enters `build`
- **THEN** the persisted `BoundWorkflowDefinitionJson` still contains the aggregate build task
- **AND** build initialization materializes the aggregate task from that snapshot
- **AND** the run does not receive synthesized lane state or the lane gate

#### Scenario: A lane-enabled run keeps its bound task definitions
- **WHEN** a run is bound with the complete six-lane definition
- **AND** the profile is edited before its `build` stage is initialized
- **THEN** stage initialization materializes the six tasks, commands, timeouts, and recovery declarations from the stored snapshot
- **AND** the current profile edit cannot change the run's lane mode or task definitions

#### Scenario: A bound definition survives reload
- **WHEN** a run with a bound definition snapshot is reloaded before a later stage starts
- **THEN** the snapshot remains available to stage initialization and lock resolution
- **AND** no current-profile lookup can replace it

### Requirement: Lane gating is scoped to the persisted workflow definition
The Server SHALL determine whether the lane gate applies from the immutable workflow definition bound when the run is initialized. A run is lane-enabled only when its persisted bound build stage contains the complete six-lane sequence in the declared order. A legacy run whose bound definition contains the aggregate `verify` task SHALL retain its existing aggregate dispatch, recovery, and stage-gate behavior; the Server SHALL NOT synthesize missing lane state, rewrite its task attempts, or make it wait for the new lanes.

#### Scenario: Legacy aggregate runs are outside the lane gate
- **WHEN** a run is loaded with a bound build definition containing the aggregate `verify` task and no six-lane sequence
- **THEN** the Server uses the existing aggregate task behavior
- **AND** it does not create six missing lane blockers
- **AND** it does not mutate or rerun the historical aggregate task as part of lane activation

#### Scenario: Runs initialized from the new definition use the lane gate
- **WHEN** a run is initialized with a bound build definition containing the complete six-lane sequence
- **THEN** the run is lane-enabled
- **AND** the Server applies ordered lane dispatch and the all-lanes-pass gate to that run
- **AND** a profile or Server deployment change cannot change the run's mode after initialization

### Requirement: Built-in workflows execute verification as ordered lanes
The built-in `mohist/local` and `mohist/github-pr` build stages SHALL represent verification as six ordered, independently reportable lanes. The lanes SHALL run in this order: dependency installation with `npm ci`; .NET verification with `dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false`; Web typecheck with `npm run typecheck -w packages/web`; Web tests with `npm run test:run -w packages/web`; Runner typecheck with `npm run typecheck -w packages/runner`; and Runner tests with `npm run test:run -w packages/runner -- --no-file-parallelism`. A later lane SHALL NOT begin until the preceding lane has passed.

#### Scenario: A clean build runs every lane once in order
- **WHEN** a built-in workflow reaches its build stage and each verification command exits successfully
- **THEN** the six lanes execute in the declared order as separate workflow work items
- **AND** each lane has one terminal result
- **AND** the build stage becomes eligible to advance only after the sixth lane passes

#### Scenario: A lane failure stops later verification
- **WHEN** a verification lane exits unsuccessfully
- **THEN** that lane records a failed result
- **AND** no later verification lane starts
- **AND** the build stage does not advance to downstream work

### Requirement: Verification commands and strictness remain unchanged
The verification lanes SHALL preserve the existing required command mapping and its strict build, typecheck, and test thresholds. The lane definitions MUST NOT add skips, allowlists, reduced test scopes, altered failure thresholds, resource-containment settings, or Runner slot-policy changes.

### Requirement: The .NET lane preserves the live runtime setup
The built-in `verify-dotnet` lane SHALL run in the same shell as `export DOTNET_ROOT=/home/szf/.dotnet` followed by `dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false`. This export is the existing project-profile prelude required by the .NET test apphosts and SHALL be present in both built-in profiles; it SHALL NOT be assumed to persist from `verify-install` or another lane because each `core/script` task has an independent shell. The export is setup for the unchanged .NET command, not an additional verification check.

#### Scenario: The lane contract includes all required checks
- **WHEN** the built-in workflow definition is inspected
- **THEN** it contains `npm ci`, the specified single-process .NET test command, both Web commands, and both Runner commands
- **AND** the Runner test command includes `--no-file-parallelism`
- **AND** no lane permits a successful result by skipping a required command or narrowing its required scope

#### Scenario: A fresh .NET lane shell receives the required runtime setup
- **WHEN** a clean representative run starts `verify-dotnet` after `verify-install` in a new `core/script` shell
- **THEN** the lane script exports `DOTNET_ROOT=/home/szf/.dotnet` before invoking `dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false`
- **AND** the .NET command can resolve the configured runtime without relying on an export from an earlier lane
- **AND** profile contract tests assert this prelude and the clean-run test completes all six lanes for each built-in profile

### Requirement: Every verification lane has an independent execution budget
Each verification lane SHALL declare and enforce its own explicit, finite execution budget. The build verification SHALL NOT be enclosed by the former full-suite `300000` millisecond timeout or by another single timeout that covers all lanes. A lane that exceeds its budget SHALL terminate as a timeout result for that lane.

#### Scenario: One slow lane times out independently
- **WHEN** a lane continues beyond its configured budget
- **THEN** the Runner terminates that lane's command and reports a timeout for the lane
- **AND** previously completed lanes retain their results
- **AND** later lanes do not start until recovery resumes the ordered sequence

#### Scenario: A fast lane is not charged for another lane's budget
- **WHEN** an earlier lane completes before its budget and a later lane consumes its own budget
- **THEN** the earlier lane remains passed with its own execution record
- **AND** the later lane's timeout or failure is attributed only to the later lane
- **AND** no aggregate deadline converts the earlier pass into an aggregate-only failure

### Requirement: Lane outcomes are durable and gate stage advancement
The system SHALL persist an observable result for every verification lane, including its lane identity, order, configured budget, terminal outcome, and failure or timeout details when applicable. A lane outcome SHALL distinguish `pass`, `fail`, and `timeout`. The build-stage gate SHALL allow advancement only when every required lane has a durable `pass` outcome.

#### Scenario: Durable results survive workflow reloading
- **WHEN** the workflow is reloaded after one or more lanes have completed
- **THEN** the completed lane results remain observable in workflow status or event projections
- **AND** a passed lane is not represented only by an aggregate verification summary
- **AND** a failed or timed-out lane remains identifiable as the lane that blocks advancement

#### Scenario: All required lanes pass before downstream work
- **WHEN** every required lane has a durable `pass` outcome
- **THEN** the build stage records verification as complete
- **AND** the next built-in workflow work, such as local checking or GitHub PR publishing, becomes eligible according to its existing order
- **AND** no downstream work becomes eligible while any lane is failed, timed out, pending, or missing a result
