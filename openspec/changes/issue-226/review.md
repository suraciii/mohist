# Review Report

## Result: FAIL

## Repaired Items

无。

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs
  Evidence: `npm test` fails at compile time. The current snapshot references `IWorkflowBacklogDirectory` and `InMemoryWorkflowBacklogDirectory` at `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:67`, but those symbols are not available to the build. The command reports `CS0246` for both names, so acceptance criteria requiring `dotnet build Mohist.sln` and `npm test` to pass are not met. [disallowed:product-behavior-change]
  SuggestedAction: Restore the correct namespace/type availability or update the registration to the current backlog registration contract, then rerun server build/tests.
  Verification: `npm test` should complete with 0 build errors and all server tests passing.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs
  Evidence: `npm test` also fails with `CS0103: The name 'JSON' does not exist in the current context` at `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs:475` and `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs:615`. This prevents any server tests from running and invalidates the claimed `npm test` PASS evidence in `openspec/changes/issue-226/progress.txt:118`. [disallowed:ambiguous-unrelated-compile-repair]
  SuggestedAction: Add the missing import/reference or update the code to the current JSON helper location, then rerun server build/tests.
  Verification: `npm test` should compile `Mohist.Server` and execute the test suite.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Infrastructure/Hosting/ConventionalRegistrationTestTypes.cs
  Evidence: Test-only probe classes are compiled into the production server assembly and are public marker services: `ConventionalScopedProbe` at `packages/server/src/Mohist.Server/Infrastructure/Hosting/ConventionalRegistrationTestTypes.cs:13`, `ConventionalSingletonProbe` at `packages/server/src/Mohist.Server/Infrastructure/Hosting/ConventionalRegistrationTestTypes.cs:26`, and `ConventionalOverrideProbe` at `packages/server/src/Mohist.Server/Infrastructure/Hosting/ConventionalRegistrationTestTypes.cs:46`. Because `AddMohistConventionalServices` scans `typeof(MohistServiceRegistration).Assembly` at `packages/server/src/Mohist.Server/Infrastructure/Hosting/ServiceCollectionExtensions.cs:28` and registers marker types, the production container now exposes synthetic test probes. This violates the intent that workflow/test artifacts are not product deliverables and broadens the production service graph with non-domain services. [disallowed:architectural-test-design-change]
  SuggestedAction: Move scanner probe types out of the product assembly, or gate them with a test-only compilation path while preserving coverage of the production scanning entry point.
  Verification: Add an assertion that no `Mohist.Server.Infrastructure.Hosting.TestTypes` services are present in the production registration, and rerun `npm test`.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Foundation/Migrated*RegistrationSpecs.cs
  Evidence: Several migrated services are exempted from fixture resolution because they require `IGrainFactory`: `AgentSessionResolver` in `packages/server/tests/Mohist.Server.Tests/Specs/Foundation/MigratedDomainServicesRegistrationSpecs.cs:139`, `WorkflowSessionHealthService` in `packages/server/tests/Mohist.Server.Tests/Specs/Foundation/MigratedWorkflowServicesRegistrationSpecs.cs:127`, and artifact/runner services in `packages/server/tests/Mohist.Server.Tests/Specs/Foundation/MigratedRunnerSystemArtifactServicesRegistrationSpecs.cs:134`. The acceptance criteria require fixture and production consistency, but for these services the tests only inspect descriptors and never prove the services resolve in a production-like Orleans fixture after migration. [disallowed:broader-test-design]
  SuggestedAction: Add coverage using the existing Orleans/Workflow fixture or another fake `IGrainFactory` setup to resolve the grain-dependent migrated services and verify their lifetimes.
  Verification: New tests should fail if any grain-dependent migrated service is marker-registered with the wrong lifetime or missing dependencies, and `npm test` should pass.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Foundation/Migrated*RegistrationSpecs.cs
  Evidence: The three migrated-service registration spec files duplicate the same descriptor and lifetime assertions, making future DI convention changes harder to maintain consistently. This is not a correctness problem in the reviewed change.
  SuggestedAction: Consider extracting a shared assertion helper or common data-driven spec once the blocking build issues are fixed.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: packages/web
  Evidence: `openspec/changes/issue-226/progress.txt:120` to `openspec/changes/issue-226/progress.txt:122` records that web typecheck passed but `npm run test:run -w packages/web` failed with 6 tests, and runner verification had not yet been run at that point. The web failures are outside the Scrutor server DI deliverable, but they remain part of the recorded post-build verification context.
  SuggestedAction: Resolve or reclassify the web test failures before using full-monorepo validation as release evidence.
  Status: out-of-scope

- [ID: item-7]
  Severity: info
  Scope: packages/runner
  Evidence: I ran `npm run typecheck -w packages/runner` and `npm test -w packages/runner`; both passed in this review snapshot. Runner is not part of the Scrutor DI deliverable.
  SuggestedAction: None.
  Status: out-of-scope

<promise>FAIL</promise>
