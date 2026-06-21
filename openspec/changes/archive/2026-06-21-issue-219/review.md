# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs` had an accidentally over-indented `var dbPath = ResolveSqliteDatabasePath(configuration);` statement inside `ResolveSqliteConnectionString`, making the refactored DB path resolver harder to read. Fixed indentation only; no behavior changed.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~Telemetry` passed: 150 passed, 1 skipped.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: formatting
  Evidence: `packages/cli/Mohist.Cli/MohistCliCommands.cs` had an unindented `root.Subcommands.Add(AgentCommands.Build(api));` line adjacent to the new `OtelCommands.Build(...)` registration. Fixed indentation only; no behavior changed.
  Verification: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter FullyQualifiedName~CliOtelCommandSpecs` passed: 18 passed.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: dependency audit
  Evidence: The server telemetry test project runs the web build as part of its build target, and npm audit output still reports 9 vulnerabilities (3 moderate, 3 high, 3 critical). This is not introduced by the OTel change and remains outside issue 219's scope.
  SuggestedAction: Track dependency audit remediation separately from the OTel collector feature.
  Status: out-of-scope

## Acceptance Criteria Evidence

- Server OTLP port wiring is implemented in `packages/server/src/Mohist.Server/Program.cs`: the app appends a Kestrel listener for `Mohist:Otel:Port` and falls back to a main-port-only app when the OTLP bind fails.
- OTLP JSON ingestion, JSON-only content negotiation, invalid JSON handling, and `{}` success responses are implemented in `packages/server/src/Mohist.Server/Api/OtlpRoutes.cs` and covered by `packages/server/tests/Mohist.Server.Tests/Specs/Telemetry/OtlpRoutesIntegrationSpecs.cs`.
- Port isolation no longer trusts the spoofable `Host` header; `packages/server/src/Mohist.Server/Api/OtlpRoutes.cs` and `packages/server/src/Mohist.Server/Otel/OtelPortIsolationMiddleware.cs` check the connection local port, with spoofed-host regression coverage in `OtlpRoutesIntegrationSpecs.cs` and `OtelQueryRoutesIntegrationSpecs.cs`.
- `otel.db` storage is isolated from the main business DB and defaults beside the configured main DB path via `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs` and `packages/server/src/Mohist.Server/Otel/OtelDb.cs`; coverage is in `OtelDbSpecs.cs`.
- Query/status APIs are implemented in `packages/server/src/Mohist.Server/Api/OtelQueryRoutes.cs` and `packages/server/src/Mohist.Server/Otel/TraceQuerier.cs`, including limit/service filters, read-only SQL execution, status counts, and collector offline reporting.
- CLI `mo otel query` and `mo otel status` are implemented in `packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs`, registered from `packages/cli/Mohist.Cli/MohistCliCommands.cs`, and covered by `packages/cli/tests/Mohist.Cli.Tests/CliOtelCommandSpecs.cs`.

## Verification

- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~Telemetry` passed: 150 passed, 1 skipped.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter FullyQualifiedName~CliOtelCommandSpecs` passed: 18 passed.

<promise>PASS</promise>
