### Requirement: SystemUpdateService obtains its clock via constructor-injected TimeProvider

`SystemUpdateService` SHALL obtain every timestamp through an injected `TimeProvider` instead of reading `DateTimeOffset.UtcNow` directly. The service SHALL expose two constructors, mirroring the existing `AttachmentService` pattern: a public production constructor that defaults the clock to `TimeProvider.System`, and an `internal` test-facing constructor that accepts an explicit `TimeProvider` instance. The injected provider SHALL be stored in a private readonly field and used by every code path that previously read the wall clock.

#### Scenario: Public production constructor defaults to the real clock

- **WHEN** `SystemUpdateService` is resolved by DI or constructed via its public constructor in production
- **THEN** the service SHALL use `TimeProvider.System` as its clock
- **AND** every timestamp it produces SHALL be the real wall-clock `DateTimeOffset`

#### Scenario: Internal constructor accepts an explicit clock for testing

- **WHEN** a test constructs `SystemUpdateService` via the `internal` constructor with a `FakeTimeProvider` instance
- **THEN** the service SHALL source every timestamp from that injected provider
- **AND** no read of `DateTimeOffset.UtcNow` SHALL occur anywhere in the service implementation

### Requirement: Every timestamp read in SystemUpdateService comes from the injected provider

All time-driven transitions in `SystemUpdateService` SHALL read "now" exclusively through `_time.GetUtcNow()`. This covers every transition site: the initial running/Building state creation in `StartAsync`; the superseded, waiting-for-reconnect, ready, and succeeded transitions in `AdvanceActiveJobAsync`; the CLI-outcome timestamp in `RecordCliOutcomeAsync`; the stale-web-job supersession in `SupersedeStaleWebJobsAsync`; the waiting-for-reconnect transition in `RunUpdateAsync`; the recovered timestamp in `TryRestoreRunnerAsync`; the command start/finish timestamps in `RunCommandAsync`; the failed timestamp in `CreateFailedTransition`; and the fallback timestamp in `ApplyTransitionLog`. Each of these reads represents "now", not a run-elapsed duration, and SHALL be sourced from the injected provider unchanged in semantics.

#### Scenario: No DateTimeOffset.UtcNow residue remains in the service file

- **WHEN** the `SystemUpdateService.cs` source is inspected after this change
- **THEN** it SHALL contain zero occurrences of `DateTimeOffset.UtcNow`
- **AND** every former call site SHALL read `_time.GetUtcNow()` instead

#### Scenario: Creation and transition timestamps come from the injected clock

- **WHEN** a job is created, advanced, recorded, superseded, recovered, or failed
- **THEN** the `CreatedAt`, `UpdatedAt`, `CompletedAt`, and log-entry `At` timestamps SHALL equal the value returned by the injected `TimeProvider.GetUtcNow()` at the moment of that transition

### Requirement: Production wiring resolves the real wall clock

The DI registration of `SystemUpdateService` SHALL remain unchanged in shape and SHALL resolve the real clock in production. `SystemUpdateService` SHALL continue to be registered as its existing singleton service kind, and `TimeProvider.System` SHALL remain the registered default so that production resolves the real wall clock without requiring a `TimeProvider` resolution for this service.

#### Scenario: Production resolves TimeProvider.System

- **WHEN** the production DI container constructs `SystemUpdateService`
- **THEN** the service SHALL use `TimeProvider.System`
- **AND** no new DI registration SHALL be required to give the service its clock

### Requirement: Time-driven transitions are deterministic under a fake clock

At least one specification SHALL drive a time-dependent transition of `SystemUpdateService` via `FakeTimeProvider.Advance` (or `SetUtcNow`) and assert on the advanced timestamp without performing any real waiting (`Task.Delay` / wall-clock polling). The `SystemUpdateServiceSpecs` test helpers (`CreateService` / `CreateConsistencyService`) SHALL thread a `FakeTimeProvider` through the `internal` constructor so that advancing the fake clock moves the service's notion of "now" forward deterministically. This unblocks deterministic coverage of the waiting-for-reconnect readiness retry, superseded-on-hash-drift, recovered/failed, and CLI-outcome timestamp branches.

#### Scenario: Waiting-for-reconnect transition timestamp is driven by the fake clock

- **WHEN** a test advances a `FakeTimeProvider` to a fixed point in time and triggers a transition (for example, the superseded-on-hash-drift or waiting-for-reconnect readiness-retry branch)
- **THEN** the persisted transition timestamp SHALL equal the fake clock's advanced value
- **AND** the assertion SHALL involve no real time waiting

#### Scenario: Spec helpers thread a FakeTimeProvider

- **WHEN** the `SystemUpdateServiceSpecs` create a service under test
- **THEN** the `CreateService` / `CreateConsistencyService` helpers SHALL inject a `FakeTimeProvider` via the `internal` constructor
- **AND** tests SHALL be able to advance that provider to drive time-dependent transitions deterministically

### Requirement: Time-source injection is behavior-preserving

Replacing the wall clock with an injected `TimeProvider` SHALL introduce no observable behavior change to the update flow. The persisted status values, stage labels, reason strings, log-entry stages and messages, the relationship between `CreatedAt`/`UpdatedAt`/`CompletedAt`, lock-release points, the 200-entry log bound, and the ordering of transitions SHALL remain identical to before this change. The injection changes only the source of "now" and its testability, not the semantics of any transition.

#### Scenario: Timestamp semantics are preserved

- **WHEN** any transition runs against the real production clock
- **THEN** the resulting timestamps SHALL carry the same meaning, format, and ordering they carried before the clock was made injectable
- **AND** state-transition timing SHALL be identical to before this change
