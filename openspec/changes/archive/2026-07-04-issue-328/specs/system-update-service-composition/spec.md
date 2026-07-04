### Requirement: Each collaborator type lives in its own file

The system-update orchestrator SHALL be decomposed so that each cohesive type (or type group) resides in its own source file under `packages/server/src/Mohist.Server/SystemInfo/`, rather than being declared inline within a single monolithic `SystemUpdateService.cs`. Specifically, the persistence repository (`FileSystemSystemUpdateStore` alongside the `ISystemUpdateStore` interface), the process command executor (`ProcessSystemUpdateCommandRunner` alongside the `ISystemUpdateCommandRunner` interface and the `SystemCommandRequest` / `SystemCommandResult` records), the HTTP readiness probe (`HttpSystemReadinessProbe` alongside the `ISystemReadinessProbe` interface and the `SystemReadinessResult` record), and the `SystemUpdateJobState` model (with its `ActiveStatuses` and `TerminalStatuses` constants) SHALL each be in a distinct file. The main `SystemUpdateService` class SHALL retain only update-job orchestration: start, the build → restart-server → wait-for-reconnect pipeline, CLI outcome recording, supersession, and runtime-consistency reporting.

#### Scenario: Persistence repository is in its own file

- **WHEN** the source tree is inspected after this change
- **THEN** `FileSystemSystemUpdateStore` and `ISystemUpdateStore` SHALL be declared in a file separate from `SystemUpdateService`
- **AND** the file-lock acquisition, atomic temp-file rename persistence, and `SaveIfCurrentAsync` optimistic-concurrency logic SHALL remain owned by that repository file

#### Scenario: Process command executor is in its own file

- **WHEN** the source tree is inspected after this change
- **THEN** `ProcessSystemUpdateCommandRunner`, `ISystemUpdateCommandRunner`, `SystemCommandRequest`, and `SystemCommandResult` SHALL be declared in a file separate from `SystemUpdateService`

#### Scenario: HTTP readiness probe is in its own file

- **WHEN** the source tree is inspected after this change
- **THEN** `HttpSystemReadinessProbe`, `ISystemReadinessProbe`, and `SystemReadinessResult` SHALL be declared in a file separate from `SystemUpdateService`
- **AND** the health-endpoint, root, and bundled-asset HTML parsing logic SHALL remain owned by that probe file

#### Scenario: Job-state model is in its own file

- **WHEN** the source tree is inspected after this change
- **THEN** the `SystemUpdateJobState` record and its `ActiveStatuses` / `TerminalStatuses` constants SHALL be declared in a file separate from `SystemUpdateService`

#### Scenario: Main service retains only orchestration

- **WHEN** the decomposed `SystemUpdateService` is inspected
- **THEN** it SHALL contain only update-job orchestration (start, build → restart-server → wait-for-reconnect, CLI outcome recording, supersession, runtime-consistency reporting)
- **AND** it SHALL NOT contain the persistence, process-execution, or HTTP-readiness implementations

### Requirement: Namespace, type, and member names are preserved

The decomposition SHALL keep every type in the `Mohist.Server.SystemInfo` namespace and SHALL preserve all existing type names and public/internal member names. No type SHALL be renamed, no interface member signature SHALL change, and no record shape SHALL change. This guarantees that DI wiring and the HTTP contract are unaffected.

#### Scenario: Namespace stays unchanged across the split

- **WHEN** any extracted type is inspected
- **THEN** its namespace declaration SHALL be `Mohist.Server.SystemInfo`
- **AND** it SHALL match the namespace it had before the split

#### Scenario: Type and member names are unchanged

- **WHEN** the extracted types are compared to their pre-split declarations
- **THEN** `FileSystemSystemUpdateStore`, `ProcessSystemUpdateCommandRunner`, `HttpSystemReadinessProbe`, `SystemUpdateJobState`, and the three interfaces SHALL keep their exact names
- **AND** every public/internal member signature (including the `SystemUpdateJobState` record positional parameters and the `ActiveStatuses` / `TerminalStatuses` constants) SHALL be unchanged

### Requirement: DI registration keeps working without wiring changes

The collaborator registrations in `MohistServiceRegistration` SHALL continue to resolve after the split with no change to the registration code. `ISystemUpdateStore` SHALL resolve to `FileSystemSystemUpdateStore`, `ISystemUpdateCommandRunner` SHALL resolve to `ProcessSystemUpdateCommandRunner`, and `ISystemReadinessProbe` SHALL resolve to `HttpSystemReadinessProbe`, each with the same lifetimes as before. `SystemUpdateService` SHALL remain a singleton `ISingletonService`.

#### Scenario: Registrations resolve to the same implementations

- **WHEN** the application starts after the split
- **THEN** `MohistServiceRegistration` SHALL register `ISystemUpdateStore → FileSystemSystemUpdateStore`, `ISystemUpdateCommandRunner → ProcessSystemUpdateCommandRunner`, and `ISystemReadinessProbe → HttpSystemReadinessProbe` without edits to the registration code
- **AND** `SystemUpdateService` SHALL still be registered as a singleton

### Requirement: HTTP and data contracts are unchanged

No public HTTP contract or persisted data shape SHALL change as a result of the split. The polling endpoint, the update-start endpoint, the CLI-outcome endpoint, and the consistency endpoint SHALL continue to accept and return the same request/response shapes. The `SystemUpdateJobState` JSON shape used by the file store SHALL remain wire-compatible.

#### Scenario: API endpoints keep their contracts

- **WHEN** the system-update HTTP endpoints are invoked after the split
- **THEN** the request and response payloads SHALL be identical in shape to those before the split
- **AND** no route, method, or status code SHALL change

### Requirement: Already-healthy sibling types are untouched

The refactor SHALL NOT modify the already-healthy sibling types in the same directory: `RuntimeBuildInfo`, `ServiceStatusChecker`, `GitSourceInspector`, `PhysicalFileSystem`, `SystemdInstallDetector`, `SystemdUnitParser`, and `SystemInfoService`. Their declarations, members, and behavior SHALL remain byte-for-byte equivalent to before this change.

#### Scenario: Sibling files are not edited

- **WHEN** the diff for this change is reviewed
- **THEN** `RuntimeBuildInfo.cs`, `ServiceStatusChecker.cs`, `GitSourceInspector.cs`, `PhysicalFileSystem.cs`, `SystemdInstallDetector.cs`, `SystemdUnitParser.cs`, and `SystemInfoService.cs` SHALL show no modifications
