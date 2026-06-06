# Spec: Test Data Factories

## ADDED Requirements

### Requirement: A Support/TestData/ directory centralizes test data factories

A `Support/TestData/` directory SHALL host static factory classes for domain entities used across multiple spec files. Each factory class SHALL be named `<Entity>TestData` and expose `Create*` methods that return either the domain object, the EF row, or both.

#### Scenario: AgentSessionTestData factory
- **WHEN** a spec needs an `AgentSession` and its `AgentSessionRow` for storage
- **THEN** it SHALL call `AgentSessionTestData.CreateRunning(...)` or `CreateTerminal(...)` or `CreateProbing(...)` instead of inlining the construction
- **AND** the factory SHALL return a tuple `(AgentSession Session, AgentSessionRow Row)` so callers do not need to re-construct either side

#### Scenario: WorkflowTestData factory
- **WHEN** a spec needs a `WorkflowRun` with a specific stage, task, or check state
- **THEN** it SHALL call `WorkflowTestData.CreateRunWithStage(...)` or similar factory method
- **AND** the factory SHALL accept named parameters for the relevant fields (projectId, stage, task, etc.)

#### Scenario: WorkflowDefinitionTestData factory
- **WHEN** a spec needs a `WorkflowDefinition` (YAML-parsed) with specific stages, tasks, or checks
- **THEN** it SHALL call `WorkflowDefinitionTestData.CreateSingleStage(...)` / `CreateWithApproval(...)` / `CreateWithCheck(...)` etc.
- **AND** the factory SHALL produce deterministic definitions (no random IDs) so test failure snapshots are reproducible

#### Scenario: IssueTestData, ProjectTestData, RepositoryTestData factories
- **WHEN** a spec needs a `Domain.Issue`, `ProjectInfo`, or `RepositoryInfo`
- **THEN** it SHALL call the corresponding `*TestData.Create*` method
- **AND** the factory SHALL accept the fields that are commonly overridden in tests (id, number, title, status, etc.) and use sensible defaults for the rest

### Requirement: WorkflowGrainSpecs helpers are extracted to static class

The `protected` instance helpers currently defined in `WorkflowGrainSpecs` (an abstract base class) SHALL be extracted to a `Support/WorkflowGrainTestHelpers.cs` static class. The 21 spec files that currently inherit from `WorkflowGrainSpecs` SHALL switch to static helper calls.

#### Scenario: Helper functions become static
- **WHEN** the helper extraction is complete
- **THEN** `RegisterRunnerAsync(...)` SHALL be `public static Task<string> WorkflowGrainTestHelpers.RegisterRunnerAsync(IGrainFactory grains, string projectId, ...)`
- **AND** the signature SHALL NOT depend on instance state (`_workflowId`, `_runnerId` fields on the base class)
- **AND** every helper that previously took a `WorkflowGrainSpecs this` parameter SHALL take an explicit `IGrainFactory grains` or `MohistDbContext db` parameter

#### Scenario: Specs stop inheriting from WorkflowGrainSpecs
- **WHEN** a spec file currently reads `public class XxxSpecs : WorkflowGrainSpecs`
- **THEN** after the refactor the class SHALL read `public class XxxSpecs` (no base)
- **AND** the constructor SHALL accept `WorkflowGrainFixture fixture` and store it in a private field
- **AND** test methods that previously called `await StartWorkflowAsync(definition)` SHALL call `await WorkflowGrainTestHelpers.StartWorkflowAsync(Grains, Db, id: "...", definition, projectId)`

#### Scenario: Mutable per-instance state is removed
- **WHEN** the helper extraction is complete
- **THEN** `_workflowId` and `_runnerId` instance fields SHALL NOT exist on any spec class
- **AND** test methods SHALL pass the workflow/runner id explicitly to helpers
- **AND** tests inside one class SHALL NOT share state through these fields

#### Scenario: WorkflowGrainSpecs.cs file is removed
- **WHEN** the 21 specs no longer inherit from `WorkflowGrainSpecs`
- **THEN** `WorkflowGrainSpecs.cs` SHALL be deleted
- **AND** the file SHALL NOT be replaced with a thin facade or a re-export module
- **AND** all references to `WorkflowGrainSpecs` in the codebase SHALL be removed

### Requirement: TestDb helper consolidates DbContext construction

A `Support/TestDb.cs` static helper SHALL provide a one-line `MohistDbContext` factory for spec files that need to bypass DI.

#### Scenario: Spec uses TestDb.NewContext
- **WHEN** a spec needs a `MohistDbContext` for direct DB operations outside the DI scope
- **THEN** it SHALL call `TestDb.NewContext(_fixture.ConnectionString)`
- **AND** the spec SHALL NOT inline `new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(cs).Options; new MohistDbContext(options);`

### Requirement: TestData factory index documents usage

A `Support/TestData/Index.md` file SHALL list each factory and its primary `Create*` methods, with one example call. The index SHALL be the entry point for contributors looking for an existing factory.

#### Scenario: New contributor looks for a factory
- **WHEN** a contributor wants to construct an `AgentSession` in a test
- **THEN** they SHALL read `Support/TestData/Index.md` first
- **AND** if a factory exists, they SHALL use it
- **AND** if no factory exists, they SHALL add one to the appropriate file rather than inlining
