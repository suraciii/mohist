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

#### Scenario: The lane contract includes all required checks
- **WHEN** the built-in workflow definition is inspected
- **THEN** it contains `npm ci`, the specified single-process .NET test command, both Web commands, and both Runner commands
- **AND** the Runner test command includes `--no-file-parallelism`
- **AND** no lane permits a successful result by skipping a required command or narrowing its required scope

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
