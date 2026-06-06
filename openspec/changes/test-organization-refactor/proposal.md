## Why

Mohist's test suite has grown to 87 spec files and 680 `[Fact]` attributes (~22K lines) without any consistent organization discipline. The cost is paid on every change:

- A one-line edit to a YAML parser drags the full 5-minute test run because nothing distinguishes a 200ms unit test from a 10s WebApplicationFactory test.
- A regression in workflow-grain lifecycle code is hard to find because 21 spec files inherit from one abstract base class (`WorkflowGrainSpecs`) named after the *system under test* but actually grouping by *behavior scenario* (`AdvanceSpecs`, `HappyPathSpecs`, `FailureSpecs`). The base class also exposes ~100 lines of `protected` instance helpers that quietly share mutable state (`_workflowId`, `_runnerId`) across tests.
- Five spec classes that don't even use `HttpClient` (`IssueQuerierSpecs`, `ProjectWorkflowProfileManagerSpecs`, `IssueWorkflowProfileManagerSpecs`, etc.) still pay the full `MohistIntegrationFixture` startup cost (WebApplicationFactory + EF + Orleans) just to access `_fixture.Services.CreateScope()`.
- Test data builders (`CreateSession`, `CreateAgentSession`, `CreateRunningSessionRow`, `BuildRootCommand`, …) are duplicated across 30+ spec files with no central home.
- Two existing test collection patterns declare `DisableParallelization = true` for fixtures that are already shared and thread-safe, so every spec in those collections runs sequentially.

This matters now because the test suite is the primary safety net for the ongoing .NET / Orleans backend refactor. A refactor that breaks 20 specs at once becomes unreviewable, and the 5-minute feedback loop is the bottleneck that pushes engineers to skip the suite. The current organization is also blocking structural improvements we already need (a project-resolution endpoint filter, a `DomainException` hierarchy, split `IssueRoutes` into per-feature files): each of those will touch 30+ test files scattered across the flat `Specs/` directory.

## What Changes

- **Categorize every test** with `[Trait("Speed", "Unit"|"Integration"|"Grain")]` and `[Trait("Sut", "<BoundedContext>")]` so IDE, CI, and `dotnet test --filter` can target subsets.
- **Split the D-class spec drift**: introduce a lightweight `MohistDbFixture` (DI + EF + Orleans silo, no `WebApplicationFactory`) and a `MohistDbCollection` for specs that use `_fixture.Services.CreateScope()` but never call `Client`.
- **Extract shared helpers and data factories** from the `WorkflowGrainSpecs` abstract base class and from inline `CreateXxx` methods into `Support/` modules. Convert 21 behavior-named subclasses from inherited state to composition of static helpers.
- **Organize spec files by Bounded Context**: move 87 flat files into `Specs/Workflow/`, `Specs/Issue/`, `Specs/Project/`, `Specs/AgentSession/`, `Specs/Skills/`, `Specs/Runner/`, `Specs/System/`, `Specs/Api/`, `Specs/Unit/` directories. Update namespaces.
- **Enforce conventions with archtest rules** so the new organization stays disciplined: spec class naming, file size limits, fixture/collection naming, and absence of `Console.WriteLine` in tests.

Out of scope:

- Not changing any production code.
- Not changing test *assertions* (refactor preserves behavior).
- Not changing the 4 existing test collections' `DisableParallelization` semantics for `MohistIntegration` (we add `MohistDb` instead, no removal).
- Not splitting the 87 files into smaller files (one spec class per file is the convention we adopt going forward, but existing files stay as one class per file already).
- Not changing the spec test framework (stay on xUnit; no migration to NUnit / xUnit v3).
- Not changing the support directory (`Support/`); only adding to it.
- Not creating a new test project (everything stays in `Mohist.Server.Tests`).

## Capabilities

### New Capabilities

- `test-categorization`: Every spec test carries `[Trait("Speed", ...)]` and `[Trait("Sut", ...)]` attributes so test subsets can be filtered by `dotnet test --filter`. CI matrix and developer workflows can run only relevant subsets.
- `fixture-sharing`: The test suite exposes three named fixture collections (`MohistIntegration` for HTTP API tests, `MohistDb` for service-level tests, `WorkflowGrain` for Orleans grain tests) with clear ownership and minimal overlap.
- `test-data-factories`: A `Support/TestData/` directory provides centralized test data factories for `AgentSession`, `WorkflowRun`, `Issue`, `Project`, `RepositoryInfo` and other domain entities used across multiple spec files.
- `bounded-context-layout`: The `Specs/` directory mirrors the bounded contexts of the production code (`Workflow`, `Issue`, `Project`, `Runner`, `AgentSession`, `Skills`, `System`, `Api`, `Unit`). Spec files live next to the SUT they exercise.
- `archtest-enforcement`: The existing `ArchitectureRules.cs` file gains four new archtest rules that prevent regression of the new organization conventions.

### Modified Capabilities

- `http-api`: No semantic change, but the API spec classes (those that use `MohistIntegrationFixture.Client`) gain `[Trait("Speed", "Integration")]` and `[Trait("Sut", "Api")]` attributes.
- `workflow-run`: No semantic change, but the workflow-grain spec classes (those that inherit from `WorkflowGrainSpecs` or use `WorkflowGrainFixture`) gain `[Trait("Speed", "Grain")]` and `[Trait("Sut", "Workflow")]` attributes.

## Impact

**Affected areas** (test code only — no production code change):

- All 87 spec files in `packages/server/tests/Mohist.Server.Tests/Specs/`
- `packages/server/tests/Mohist.Server.Tests/Support/` gains 4–6 new helper files and 1 new fixture class
- `packages/server/tests/Mohist.Server.Tests/Architecture/ArchitectureRules.cs` gains 4 new rules
- `packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj` may add `<ItemGroup>` for spec subfolder `<Compile>` includes if globbing fails
- CI scripts that invoke `dotnet test` may be updated to use `--filter "Speed=Unit"` for fast feedback

**Affected tests** (rough count):

- 680 `[Fact]`/`[Theory]` tests gain trait attributes (mechanical)
- 5–7 spec classes migrate from `MohistIntegrationCollection` to `MohistDbCollection`
- 21 spec files change their inheritance / import structure (helper access pattern)
- 87 spec files move to new subdirectories (file path change only)
- 0 spec file changes its test logic or assertions

**Verification**:

- All 680+ existing tests still pass after each phase.
- `dotnet test --filter "Speed=Unit"` runs in under 30 seconds.
- `dotnet test --filter "Speed=Integration"` runs only API integration tests.
- `dotnet test --filter "Sut=Workflow"` runs only workflow tests.
- Archtest rules reject any new spec that violates naming/size/conventions.

**Risk**:

- Phase 1 (categorization) is zero-risk: adding attributes doesn't change behavior.
- Phase 2 (fixture split) is low-risk: spec authors explicitly use `_fixture.Services.CreateScope()` without `Client`; they get a lighter fixture with same services.
- Phase 3 (helper extraction) is medium-risk: helper semantics must be preserved exactly; covered by full test suite.
- Phase 4 (file relocation) is low-risk mechanically, but high *cognitive* risk: 87 file moves + 87 namespace updates + 87 `using` directive updates need to be done in lockstep.
- Phase 5 (archtest) is low-risk: existing tests already pass; new rules reject only deliberate violations.
