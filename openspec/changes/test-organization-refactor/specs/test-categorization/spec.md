# Spec: Test Categorization

## ADDED Requirements

### Requirement: Every spec test carries Speed and Sut traits

Every `[Fact]` and `[Theory]` in `Mohist.Server.Tests.Specs` SHALL carry exactly one `[Trait("Speed", ...)]` attribute and at least one `[Trait("Sut", ...)]` attribute, so the test suite is filterable by `dotnet test --filter`.

#### Scenario: Speed trait values
- **WHEN** a spec test does no I/O and uses no fixture
- **THEN** it SHALL carry `[Trait("Speed", "Unit")]`
- **AND** when it uses `WorkflowGrainFixture`, `BacklogFixture`, or any other in-process Orleans cluster fixture, it SHALL carry `[Trait("Speed", "Grain")]`
- **AND** when it goes through `MohistIntegrationFixture.Client` it SHALL carry `[Trait("Speed", "Integration")]`
- **AND** when it uses `MohistDbFixture` (introduced by the `fixture-sharing` capability) it SHALL carry `[Trait("Speed", "Service")]`

#### Scenario: Sut trait values mirror production namespaces
- **WHEN** a spec test exercises a Bounded Context in production code
- **THEN** it SHALL carry a `[Trait("Sut", "<BoundedContext>")]` attribute
- **AND** the Bounded Context names SHALL be one of `Workflow`, `Issue`, `Project`, `Epic`, `Runner`, `AgentSession`, `Skills`, `System`, `Api`, `Architecture`, `Foundation`
- **AND** cross-cutting tests that exercise two or more contexts SHALL carry one trait per context

#### Scenario: Trait filtering works at the test runner level
- **WHEN** a developer runs `dotnet test --filter "Speed=Unit"`
- **THEN** only spec tests with `[Trait("Speed", "Unit")]` SHALL run
- **AND** the command SHALL complete in under 30 seconds on a developer workstation
- **AND** `dotnet test --filter "Sut=Workflow"` SHALL run only workflow-related tests
- **AND** the same filtering SHALL work under any xUnit v2 runner (Visual Studio Test Explorer, `dotnet test`, NCrunch)

### Requirement: Trait vocabulary is documented in code

A `Support/Traits.cs` file SHALL document the allowed `Speed` and `Sut` values, so contributors adding new tests cannot pick arbitrary string values.

#### Scenario: Adding a new spec test
- **WHEN** a contributor writes a new `[Fact]` without trait attributes
- **THEN** the `SpecFiles_MustHaveSpecOrCollectionSuffix` archtest rule (introduced by the `archtest-enforcement` capability) SHALL flag the missing traits in CI
- **AND** the contributor SHALL consult `Support/Traits.cs` for the allowed values
- **AND** the contributor SHALL add `[Trait("Speed", "...")]` and at least one `[Trait("Sut", "...")]` before merging

### Requirement: Trait attributes are removed only with explicit justification

Removing a `[Trait]` attribute from an existing test SHALL be reviewed as a behavior change because it alters which CI matrix the test runs in. The PR description SHALL state which `Speed` or `Sut` value is being removed and why.

#### Scenario: Promoting a test from Service to Integration
- **WHEN** a test is migrated from `MohistDbFixture` to `MohistIntegrationFixture.Client` (i.e., from direct DB seeding to HTTP request)
- **THEN** its `Speed` trait SHALL change from `Service` to `Integration`
- **AND** the change SHALL be visible in the diff
- **AND** the PR description SHALL note the speed promotion
