# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: none
  Evidence: No repair was applied. The open issues require either acceptance-scope judgment or behavior/test fixes outside the allowed small local repair policy.
  Verification: Not applicable.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:113
  Evidence: The update facade constructor still injects ten primary collaborators/infrastructure values plus optional timeout/unit/home parameters, and the class still stores raw `IServiceInstaller`, `ICommandExecutor`, `IFileSystem`, `IEnvironmentVariableProvider`, and `HttpClient` fields at `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:99`. This does not satisfy the issue/spec requirement that the facade dependency surface be reduced from the prior 12-item god-class constructor to "few collaborators + output" and "strictly fewer than 12". The extracted validator/probe/verifier exist, but the facade still receives most of their underlying infrastructure directly and also exposes a legacy 12-shape construction bridge at `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:153`. [disallowed:architectural-judgment]
  SuggestedAction: Rework `SourceCodeUpdater` so its public construction surface only accepts the dependencies it actually owns for orchestration/finalization plus `RuntimeConsistencyValidator`, `ServiceReadinessProbe`, and `RunnerRefreshVerifier`; remove or confine the legacy 12-shape bridge if it is not required by product behavior, and update tests to construct collaborators directly.
  Verification: Inspect the constructor signature and fields, then rerun `dotnet build Mohist.sln` and `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj`.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: packages/runner/tests/acp-agent.spec.ts:200
  Evidence: The candidate changes include runner code/tests/config outside the CLI refactor scope, and the runner suite fails on the current snapshot: `npm test -w packages/runner` reports `ProbeTimesOutWithoutQualifyingActivity_LivenessMonitored_FailsSession` expected `failure` but received `success` at `packages/runner/tests/acp-agent.spec.ts:200`, and `ExistingSharedSessionStreamsThoughtChunks_ProbeWindowCrossed_DoNotTimeoutOrAppendThoughtText` expected one prompt call but observed two at `packages/runner/tests/acp-agent.spec.ts:265`. Because `packages/runner/src/runtime/executor.ts`, `packages/runner/tests/acp-agent.spec.ts`, and `packages/runner/vitest.config.ts` are changed in this candidate, this is not treated as an out-of-scope pre-existing failure. [disallowed:product-behavior/test-repair]
  SuggestedAction: Either revert the unrelated runner changes from this issue branch or fix the ACP liveness behavior/tests so the full changed runner suite passes deterministically.
  Verification: Rerun `npm run typecheck -w packages/runner` and `npm test -w packages/runner`; both should pass.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/cli/Mohist.Cli/InfoCollector.Verbose.cs:8
  Evidence: The issue specifically targets `InfoCollector.cs` as a file with mixed collection/rendering/systemd responsibilities and acceptance says the environment information collector is separated from renderer and systemd parser into independent types. Rendering and systemd parsing are extracted, but a large `InfoCollector.Verbose.cs` partial remains as part of the collector with verbose environment/API/disk/skill/source collection logic. This likely keeps the collector split by file size rather than a single focused collector file, and it risks missing the "one file only changes for one reason" goal. [disallowed:architectural-judgment]
  SuggestedAction: Reassess whether verbose collection should be an independent collector collaborator or whether the spec/task should explicitly allow collector partials for verbose collection. If keeping the partial, provide complexity evidence that all resulting collector files are outside the top-five scc target and document the responsibility boundary.
  Verification: Inspect `InfoCollector*.cs` responsibilities and run the scc ranking required by T-004.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: acceptance evidence
  Evidence: The issue requires the three target files and successors to leave the CLI package top-five scc complexity list and requires byte-for-byte unchanged CLI outputs/arguments/exit codes. I found passing build and CLI tests, but no candidate evidence in `review.md`/artifacts proving scc top-five ranking or an actual before/after byte-level output comparison for `mo update`, `mo info`, and representative table commands. Tests are useful regression coverage, but they are not a direct byte-for-byte comparison against the pre-refactor snapshot. [disallowed:acceptance-evidence]
  SuggestedAction: Add recorded scc output for `packages/cli/Mohist.Cli/` and run/record explicit before-after command output comparisons or a golden-output harness for the affected CLI commands.
  Verification: Provide the scc ranking values and byte comparison command outputs, then rerun `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj`.
  Status: open

## Follow-up Items

- [ID: item-6]
  Severity: follow-up
  Scope: packages/cli/Mohist.Cli/Update/RuntimeConsistencyValidator.cs:15
  Evidence: `RuntimeConsistencyValidator` and `ServiceReadinessProbe` both carry their own identical asset-path regex and HTML asset probing logic. This is not a correctness issue in the refactor, but future changes to the web readiness contract now need to update two places.
  SuggestedAction: Consider a tiny shared internal helper only if another readiness/web-asset check is added; avoid adding it now unless duplication starts causing maintenance bugs.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: warning
  Scope: npm audit
  Evidence: `dotnet build Mohist.sln` invokes the web build and reports `npm audit` findings: 9 vulnerabilities, including 3 moderate, 3 high, and 3 critical. These are dependency posture findings surfaced during validation and are not introduced by the CLI refactor itself based on the reviewed diff.
  SuggestedAction: Track dependency remediation separately from issue #254.
  Status: out-of-scope

## Verification Performed

- `mo issue show 254 --project-id proj_f6c141d63b6243bfbb481737b2243b87`: read current issue details and acceptance criteria.
- `dotnet build Mohist.sln`: passed with 0 warnings and 0 errors.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj`: passed, 150 tests.
- `npm run typecheck -w packages/runner`: passed.
- `npm run typecheck -w packages/web`: passed.
- `npm run test:run -w packages/web`: passed, 116 files and 1696 tests with 1 skipped.
- `npm test -w packages/runner`: failed with 2 ACP liveness test failures described in item-3.
- `npm test -w packages/cli`: command is invalid in this repository (`No workspaces found: --workspace=packages/cli`); replaced with the CLI dotnet test command above.

<promise>FAIL</promise>
