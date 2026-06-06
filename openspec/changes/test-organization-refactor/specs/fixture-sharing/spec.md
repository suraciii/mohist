# Spec: Fixture Sharing

## ADDED Requirements

### Requirement: Three named test fixture collections

The test suite SHALL expose exactly three named xUnit collection fixtures used by integration-style specs, plus the existing single-file collections:

| Collection | Fixture | Used by | Purpose |
|------------|---------|---------|---------|
| `MohistIntegration` | `MohistIntegrationFixture` | HTTP API specs | Full `WebApplicationFactory<Program>` stack, exposes `HttpClient`. |
| `MohistDb` | `MohistDbFixture` (NEW) | Service-level specs | DI container + EF + Orleans silo, no `WebApplicationFactory`. Exposes `Services` and `Grains` but no `Client`. |
| `WorkflowGrain` | `WorkflowGrainFixture` | Orleans grain specs | In-process Orleans cluster with shared event bus / event store. |
| `Backlog` | `BacklogFixture` | One spec | Single-spec collection for the in-memory backlog test. |

#### Scenario: HTTP API spec belongs to MohistIntegration
- **WHEN** a spec calls `_fixture.Client.GetAsync(...)` or any of the `ApiTestClient` extension methods
- **THEN** the spec class SHALL be marked `[Collection("MohistIntegration")]`
- **AND** its constructor SHALL accept `MohistIntegrationFixture`

#### Scenario: Service-level spec belongs to MohistDb
- **WHEN** a spec accesses `_fixture.Services.CreateScope()` or `_fixture.Grains.GetGrain<...>()` but never `_fixture.Client`
- **THEN** the spec class SHALL be marked `[Collection("MohistDb")]`
- **AND** its constructor SHALL accept `MohistDbFixture`
- **AND** the spec SHALL NOT pay the `WebApplicationFactory<Program>` startup cost

#### Scenario: Orleans grain spec belongs to WorkflowGrain
- **WHEN** a spec uses `WorkflowGrainFixture` directly via `IClassFixture<WorkflowGrainFixture>` or via inheritance from a shared base
- **THEN** the spec class SHALL be marked `[Collection("WorkflowGrain")]`
- **AND** its constructor (or base class) SHALL accept `WorkflowGrainFixture`

### Requirement: MohistDbFixture shares the production service graph

`MohistDbFixture` SHALL register the same set of services as `MohistServiceRegistration.AddMohistServerCore`, by reusing a shared `ConfigureMohistServices` extension method. This ensures drift between production and tests is caught at compile time or at the first test that fails.

#### Scenario: Service graph mirrors production
- **WHEN** `MohistDbFixture.InitializeAsync` builds the DI container
- **THEN** it SHALL call the same `ConfigureMohistServices(IServiceCollection, IConfiguration)` method that the real server uses
- **AND** the only differences SHALL be: (1) test-only `IFileSystem` is replaced with `FakeFileSystem`, (2) `IGitService` is replaced with `FakeGitService`, (3) `IEnvironmentVariableProvider` is replaced with `MockEnvironmentVariableProvider`

#### Scenario: Service graph drift is caught
- **WHEN** production code adds a new service registration in `MohistServiceRegistration.AddMohistServerCore`
- **THEN** the same registration SHALL be picked up by `MohistDbFixture` automatically
- **AND** no test SHALL fail to find a service that production code provides

### Requirement: Each collection can run in parallel with the others

xUnit SHALL be allowed to run specs in different collections concurrently. A spec that needs strict sequential execution SHALL declare its own `DisableParallelization` on its specific collection.

#### Scenario: Three collections run concurrently
- **WHEN** the developer runs `dotnet test` without filters
- **THEN** xUnit SHALL be free to run `MohistIntegration`, `MohistDb`, and `WorkflowGrain` collections in parallel
- **AND** the total wall-clock test time SHALL be less than the sum of the three collection times
- **AND** a previously disabled parallelization flag SHALL NOT be re-introduced by accident

#### Scenario: Single-fixture collections stay single
- **WHEN** a collection contains exactly one spec (e.g., `Backlog`, `WorkflowEvents`)
- **THEN** the collection definition MAY remain as-is to keep the spec isolated from others
- **AND** a future change MAY fold the spec into a sibling collection if the shared fixture is identical

### Requirement: MohistCollections consolidates collection definitions

A single `Support/MohistCollections.cs` file SHALL host all `[CollectionDefinition(...)]` and `[Collection(...)]` attributes. Inline collection definitions inside spec files SHALL be removed.

#### Scenario: Collection definitions are discoverable
- **WHEN** a developer searches for `CollectionDefinition` in the test project
- **THEN** every result SHALL live in `Support/MohistCollections.cs`
- **AND** no spec file SHALL declare its own `[CollectionDefinition]`

#### Scenario: Spec files declare only their collection membership
- **WHEN** a spec class wants to be in a collection
- **THEN** it SHALL carry `[Collection("Name")]` only
- **AND** the corresponding `[CollectionDefinition("Name", ...)]` SHALL be in `MohistCollections.cs`
