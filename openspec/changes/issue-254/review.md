# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/cli/Mohist.Cli/VerboseRunnerInspector.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/CliInfo/InfoCollectorVerboseSpecs.cs`
  Evidence: `VerboseRunnerInspector.GetCapacityVerboseAsync` now only reads `capacity.active` from `/api/projects/{projectId}/agent/status` and returns `new InfoVerboseCapacity(active, maxFromUnit ?? maxFromEnv)`. The pre-refactor implementation also read `capacity.max` from the server response and used it when no systemd/env max was available. As a result, `mo info --verbose` reports `Runner capacity: max: <unknown>` for a valid server response such as `{ "capacity": { "active": 2, "max": 8 } }` whenever `MAX_CONCURRENT_WORKFLOWS` is absent from the unit/environment, violating the no behavior change acceptance criterion. Existing tests cover unit/env max plus active-from-server (`Verbose_Capacity_ReadsMaxFromSystemdEnvAndActiveFromServer`) but do not cover the server-only max fallback, so the regression survives the focused suites. [disallowed:reason] Repair would change product behavior and should be made by the build task, not silently during review.
  SuggestedAction: Restore the server `capacity.max` fallback in `VerboseRunnerInspector`, preferably by parsing both `active` and `max` from the API response and applying the original precedence `unit max -> server max -> environment max`. Add a focused test where the API returns `capacity.max`, systemd has no `MAX_CONCURRENT_WORKFLOWS`, and the environment is unset.
  Verification: Run a new/updated capacity test plus `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~CliInfo"` and `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj`.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: npm dependencies
  Evidence: The affected server-spec verification completed successfully, but the build step emitted npm audit output: `9 vulnerabilities (3 moderate, 3 high, 3 critical)`. This is unrelated to the issue-254 CLI refactor candidate.
  SuggestedAction: Track npm dependency remediation separately.
  Status: out-of-scope

- [ID: item-3]
  Severity: info
  Scope: verification
  Evidence: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj` passed: 151 passed, 0 failed. `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~CliInfo|FullyQualifiedName~SystemSpecs.UpdateSpecs|FullyQualifiedName~Skills.UpdateInstallSyncSpecs"` passed: 133 passed, 0 failed. `scc packages/cli/Mohist.Cli --by-file --format csv --sort complexity` top five were `WindowsScheduledTaskInstaller.cs` complexity 88, `MohistCliCommands.Issue.cs` 86, `DeliveryFailureGuidance.cs` 79, `MohistCliCommands.Agent.cs` 53, and `SystemdServiceInstaller.cs` 52, so the issue-254 successor files are no longer in the top five.
  SuggestedAction: Keep these commands as verification after resolving the capacity regression.
  Status: out-of-scope

<promise>FAIL</promise>
