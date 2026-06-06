# Spec: Bounded-Context Layout

## ADDED Requirements

### Requirement: Spec files live under Bounded-Context subdirectories

The `Specs/` directory SHALL contain a subdirectory per Bounded Context that mirrors the production layout. Each spec file SHALL be located under the subdirectory that matches the production namespace the spec exercises.

#### Scenario: Workflow tests live under Specs/Workflow/
- **WHEN** a spec exercises `Mohist.Server.Workflow.*` (any sub-namespace)
- **THEN** the spec file SHALL live under `Specs/Workflow/...`
- **AND** within `Specs/Workflow/`, subdirectories mirror the production split: `Grain/`, `Querier/`, `Api/`, `Domain/` (if needed)

#### Scenario: Issue tests live under Specs/Issue/
- **WHEN** a spec exercises `Mohist.Server.Issue.*`
- **THEN** the spec file SHALL live under `Specs/Issue/...`
- **AND** the same sub-folder convention applies: `Grain/`, `Querier/`, `Api/`, `Domain/`, `Profile/`

#### Scenario: Cross-cutting tests live under Specs/Foundation/ or Specs/Api/
- **WHEN** a spec exercises types that don't belong to a single Bounded Context (e.g., `PromptTemplateEngine`, `VariableBundle`, `EventBus`)
- **THEN** the spec file SHALL live under `Specs/Foundation/`
- **WHEN** a spec exercises only HTTP API shape and contracts without a specific SUT
- **THEN** the spec file SHALL live under `Specs/Api/`

#### Scenario: Architecture tests live under Specs/Architecture/
- **WHEN** a spec is an ArchUnitNET rule
- **THEN** the spec file SHALL live under `Specs/Architecture/` (or remain at the existing `Architecture/` root if the project convention keeps ArchUnitNET rules separate)

### Requirement: Spec file names follow SUT-prefix convention

Each spec file's name SHALL start with the SUT name (the Bounded Context entity) and end with `Specs` or `Collection`. Behavior-scenario names (e.g., `AdvanceSpecs`, `HappyPathSpecs`) SHALL be merged into a single SUT-named file with the original behavior preserved as method names.

#### Scenario: Workflow grain tests consolidated by SUT
- **WHEN** the file relocation is complete
- **THEN** the 21 behavior-named workflow spec files SHALL be consolidated into SUT-named files, e.g.:
  - `AdvanceSpecs.cs` + `HappyPathSpecs.cs` + `PausingWorkSpecs.cs` → `WorkflowGrainLifecycleSpecs.cs`
  - `FailureSpecs.cs` → `WorkflowGrainFailureSpecs.cs`
  - `CheckRetrySpecs.cs` + `ChecksParallelSpecs.cs` → `WorkflowGrainCheckSpecs.cs`
  - `RetryAndRerunSpecs.cs` + `RetryRerunSpecs.cs` → `WorkflowGrainRetrySpecs.cs`
  - `BoundarySpecs.cs` + `VariableScopeSpecs.cs` → `WorkflowGrainVariableSpecs.cs`
  - `ApprovalGateSpecs.cs` → `WorkflowGrainApprovalSpecs.cs`
  - `StageLockSpecs.cs` → `WorkflowGrainStageLockSpecs.cs`
  - `DispatchAndLoadingSpecs.cs` → `WorkflowGrainDispatchSpecs.cs`
  - `StatusSpecs.cs` + `WorkflowStateSpecs.cs` → `WorkflowGrainStatusSpecs.cs`
  - `WorkflowLeaseActivationSpecs.cs` + `WorkflowRetrySpecs.cs` → `WorkflowGrainLeaseSpecs.cs`
  - `RunnerBindingSpecs.cs` + `RunnerFailureSpecs.cs` + `RunnerRegistrySpecs.cs` + `RunnerStatusProjectionSpecs.cs` → `RunnerGrainSpecs.cs`
- **AND** the original test method names SHALL be preserved (e.g., `Advance_ToNextStage_EmitsEvents`)

#### Scenario: Large spec files split when moved
- **WHEN** a spec file is over 600 lines and is being relocated
- **THEN** it SHALL be split into multiple SUT-named files along natural boundaries (e.g., `AgentSessionSpecs.cs` → `AgentSessionSetupSpecs.cs` + `AgentSessionTelemetrySpecs.cs` + `AgentSessionEndOfRunSpecs.cs`)
- **AND** the `SpecFiles_MustStayBellowSizeBudget` archtest rule SHALL be satisfied after the split

#### Scenario: Single-class-per-file convention
- **WHEN** a new spec file is added
- **THEN** it SHALL contain exactly one `public class *Specs` declaration
- **AND** it SHALL NOT contain helper classes that could live in `Support/`
- **AND** the archtest rule `SpecFiles_MustHaveSpecOrCollectionSuffix` SHALL reject file names that do not end with `Specs` or `Collection`

### Requirement: Spec namespaces follow directory structure

Each spec file's `namespace` declaration SHALL mirror its directory path under `Specs/`, prefixed with `Mohist.Server.Tests.Specs`.

#### Scenario: Specs/Workflow/Grain/WorkflowGrainLifecycleSpecs.cs
- **WHEN** a spec file lives at `Specs/Workflow/Grain/WorkflowGrainLifecycleSpecs.cs`
- **THEN** its `namespace` declaration SHALL be `namespace Mohist.Server.Tests.Specs.Workflow.Grain;`
- **AND** other spec files in the same subdirectory SHALL share this namespace

#### Scenario: Cross-Bounded-Context specs use cross-context namespaces
- **WHEN** a spec file lives at `Specs/Foundation/VariableBundleSpecs.cs`
- **THEN** its `namespace` declaration SHALL be `namespace Mohist.Server.Tests.Specs.Foundation;`
- **AND** it SHALL NOT use a Bounded-Context-specific namespace

### Requirement: csproj globbing picks up all spec files

The `Mohist.Server.Tests.csproj` SHALL NOT need to add `<Compile Include="..." />` entries for the new subdirectories. The default SDK glob picks up `**/*.cs` recursively.

#### Scenario: Build discovers all relocated specs
- **WHEN** `dotnet build packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj` runs after the file relocations
- **THEN** the build output SHALL include all 87 (or 80+, after consolidation) spec files
- **AND** no spec file SHALL be silently omitted because of an explicit `<Compile>` whitelist in the csproj

### Requirement: New file moves do not break test discovery

After the file relocations, the test runner SHALL still discover every `[Fact]` and `[Theory]` method, regardless of the new namespace or file path.

#### Scenario: Test counts preserved
- **WHEN** the file relocations are complete
- **THEN** `dotnet test --list-tests | wc -l` SHALL equal the pre-relocation count (currently 680)
- **AND** no test SHALL be silently skipped or duplicated

### Requirement: Architecture rule tolerates new test subfolder layout

The existing `FeatureDirectories_ShouldOnlyContainDomainGrainsAndServices` archtest rule applies to production source under `packages/server/src/Mohist.Server/`. It SHALL NOT block the new `Specs/` subdirectory layout.

#### Scenario: Production rule stays scoped to production
- **WHEN** the rule iterates source files
- **THEN** it SHALL only consider `packages/server/src/Mohist.Server/*` (production code)
- **AND** it SHALL NOT consider `packages/server/tests/Mohist.Server.Tests/Specs/...` (test code)
- **AND** the new `SpecFiles_MustStayBellowSizeBudget` rule covers the test side
