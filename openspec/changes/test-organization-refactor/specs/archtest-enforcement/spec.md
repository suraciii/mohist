# Spec: Archtest Enforcement

## ADDED Requirements

### Requirement: Spec files must end with Specs or Collection

The `Architecture/ArchitectureRules.cs` file SHALL host a `SpecFiles_MustHaveSpecOrCollectionSuffix` archtest rule that walks the `Specs/` directory and fails the build if any spec file's name does not end with `Specs` or `Collection` (or `Index.md`).

#### Scenario: New spec file with wrong name
- **WHEN** a contributor adds a file `Specs/Issue/MyTest.cs` (no `Specs` suffix)
- **THEN** the archtest SHALL fail with the message "Spec files must end with 'Specs' or 'Collection'. Violations: MyTest.cs"
- **AND** the contributor SHALL rename the file to `MyTestSpecs.cs` to make the test pass

#### Scenario: Helper class file with correct name
- **WHEN** a contributor adds a file `Specs/Workflow/Fixtures/WorkflowGrainCollection.cs`
- **THEN** the archtest SHALL pass because the file ends with `Collection`

#### Scenario: Index file
- **WHEN** a contributor adds `Support/TestData/Index.md`
- **THEN** the archtest SHALL skip it because the rule only considers `*.cs` files

### Requirement: Spec files must stay under the size budget

The `Architecture/ArchitectureRules.cs` file SHALL host a `SpecFiles_MustStayBellowSizeBudget` archtest rule that fails the build if any spec file's source size exceeds ~24 KB (≈600 lines × 40 chars/line).

#### Scenario: Spec file over budget
- **WHEN** a spec file grows beyond 24 KB
- **THEN** the archtest SHALL fail with a list of files exceeding the budget
- **AND** the contributor SHALL split the file into smaller files along natural test boundaries (lifecycle phase, behavior scenario, etc.)

#### Scenario: Just-under-budget file
- **WHEN** a spec file is 23 KB
- **THEN** the archtest SHALL pass without warning

#### Scenario: Budget threshold is configurable
- **WHEN** a future change wants to relax the budget
- **THEN** the threshold SHALL be a single `const int SpecFileSizeBudgetBytes = 24_000;` at the top of the rule
- **AND** changing the constant is the only way to change the threshold

### Requirement: Spec class declarations must be public

The `Architecture/ArchitectureRules.cs` file SHALL host a `SpecClasses_MustBePublic` archtest rule that fails the build if a `*Specs` class is declared as `internal` or with non-`public` access.

#### Scenario: Internal spec class
- **WHEN** a contributor declares `internal class IssueDomainSpecs` (instead of `public class IssueDomainSpecs`)
- **THEN** the archtest SHALL fail with the file name and line number
- **AND** the contributor SHALL change the access modifier to `public`

#### Scenario: Private nested spec class
- **WHEN** a contributor declares a `private class MySpecs` nested inside another class
- **THEN** the archtest SHALL pass because the rule only checks top-level `*Specs` classes
- **AND** private nested specs are not a common pattern in this codebase

### Requirement: Spec namespaces must live under Specs/

The `Architecture/ArchitectureRules.cs` file SHALL host a `SpecNamespaces_MustBeUnderSpecs` archtest rule that fails the build if any spec file's `namespace` declaration is not under `Mohist.Server.Tests.Specs`.

#### Scenario: Wrong spec namespace
- **WHEN** a contributor adds a spec file with `namespace Mohist.Server.Tests;` (missing the `.Specs` suffix)
- **THEN** the archtest SHALL fail with the offending namespace
- **AND** the contributor SHALL fix the namespace to `Mohist.Server.Tests.Specs.<BoundedContext>`

#### Scenario: Test in a different subdirectory
- **WHEN** a test lives under `Architecture/ArchitectureRules.cs` (already at the project root, outside `Specs/`)
- **THEN** the archtest SHALL skip this file because it only iterates files under `Specs/`
- **AND** the file's namespace `Mohist.Server.Tests.Architecture` is not enforced by this rule

### Requirement: Archtest rules are pure and deterministic

All four new archtest rules SHALL be deterministic (no time-of-day, no random, no filesystem race) and SHALL NOT modify any file or external state.

#### Scenario: Running archtest twice
- **WHEN** the developer runs `dotnet test --filter "FullyQualifiedName~SpecFiles_..."` twice in a row
- **THEN** both runs SHALL produce the same pass/fail result
- **AND** the file enumeration order SHALL not matter (output is sorted)

#### Scenario: Archtest does not modify files
- **WHEN** an archtest rule runs
- **THEN** it SHALL only read files
- **AND** it SHALL NOT write to, delete, or rename any file
- **AND** a follow-up `git status` SHALL show no changes
