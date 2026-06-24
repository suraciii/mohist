# Review Report

## Result: PASS

## Repaired Items

无。

## Blocking Items

无。

## Follow-up Items

无。

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: warning
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Foundation/HttpApiJsonWiringSpecs.cs
  Evidence: `npm test` compiles and runs the server suite, but currently fails `HttpApiJsonWiringSpecs.SignalRJsonHubProtocolOptions_PayloadSerializerOptionsIsUnifiedFacade` at `packages/server/tests/Mohist.Server.Tests/Specs/Foundation/HttpApiJsonWiringSpecs.cs:124` with `Assert.Same() Failure` for SignalR JSON serializer options. This is outside the Scrutor DI registration change: focused DI verification passes 108 tests, and the failing test exercises JSON/SignalR wiring rather than service scanning, marker lifetimes, migrated service resolution, or explicit-registration precedence.
  SuggestedAction: Handle the SignalR JSON options regression in the owning JSON/HTTP wiring area before relying on full-suite `npm test` as release evidence.
  Status: out-of-scope

## Review Notes

- Issue/spec alignment: `Directory.Packages.props:36` and `packages/server/src/Mohist.Server/Mohist.Server.csproj:26` add Scrutor while keeping `Microsoft.Extensions.DependencyInjection`; `ServiceCollectionExtensions.AddMohistConventionalServices` scans `typeof(MohistServiceRegistration).Assembly` by default and maps `IScopedService`/`ISingletonService` to scoped/singleton `AsSelf()` registrations.
- Registration ordering: `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:49` calls conventional scanning before explicit registrations, preserving the intended “hand-written registrations win” behavior.
- Migration coverage: marker search shows 34 production marker services, including `WorkflowRunProfileManager`; focused tests cover descriptor lifetimes, fixture resolution, explicit override behavior, production exclusion of test probes, and grain-backed migrated services.
- Verification: focused DI command passed with 108 tests. Full `npm test` compiled and ran, then failed only the out-of-scope JSON wiring test noted above.

<promise>PASS</promise>
