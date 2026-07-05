# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/cli/Mohist.Cli/Update/RuntimeConsistencyValidator.cs`
  Evidence: The injected runner identity timeout does not bound an in-flight `/api/runner/identity` request. `PollForRunnerIdentityAsync` computes a deadline at lines 222-239, but each attempt awaits `TryGetRunnerIdentityAsync(token)` using only the caller token. `TryGetRunnerIdentityAsync` then passes that same token into `HttpClient.GetAsync`/stream parsing at lines 285-299. If the identity endpoint accepts the request but hangs longer than `_runnerIdentityTimeout` (or if `HttpClient.Timeout` is longer/infinite), the check cannot return the required timeout Warn at the injected deadline. This violates the issue/spec requirement that the verification window is bounded by the injectable timeout. [disallowed:product-behavior-change]
  SuggestedAction: Apply the runner identity deadline to the HTTP attempt itself, for example with a linked cancellation token driven by the injected `TimeProvider`, and preserve caller cancellation semantics. Add a unit test whose handler/content never completes and verify `CheckRunnerIdentityAsync` returns the `did not respond` Warn when fake time reaches the configured timeout, without consuming wall-clock time.
  Verification: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter RuntimeConsistencyValidatorSpecs` passed 21/21 in 45 ms; existing tests do not cover a hanging in-flight identity request.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs`
  Evidence: `SourceCodeUpdater.CreateWithDefaults` accepts `runnerIdentityTimeout` and `timeProvider` at lines 153-156, but constructs `RuntimeConsistencyValidator` at line 166 without passing either value. They are passed to `ServiceReadinessProbe`, `RunnerRefreshVerifier`, and `SourceCodeUpdater` at lines 167-184, but not to the new polling validator used by the full `VerifyRuntime` stage. This leaves the full `mo update` factory path unable to exercise the new runner-identity poll with fake time or a shortened timeout, so the post-build snapshot lacks meaningful full-flow regression coverage for the reported false warning. [disallowed:product-behavior-change]
  SuggestedAction: Thread the existing `timeProvider` and runner identity timeout into `RuntimeConsistencyValidator` from `CreateWithDefaults` (and expose/pass a poll interval if the factory needs configurable interval coverage). Add a `VerifyRuntime` or `UpdateAll` spec where `/api/runner/identity` is initially unavailable and later returns the matching hash, proving the full stage prints `[ok] Runner identity` without real time.
  Verification: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj` passed 660/660 in 230 ms, but no full-flow delayed-identity test exists; current `SourceCodeUpdaterVerifyRuntimeSpecs` only covers immediate runner identity availability/mismatch/dry-run.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `openspec/changes/issue-364/tasks.json`
  Evidence: The task record still has `"passes": false` at line 26 even though the implementation is present and the focused/full CLI tests pass. This is not a product deliverable defect, but it weakens workflow traceability for later readers.
  SuggestedAction: Update the workflow task status when the implementation is accepted.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: dependency audit output during server test build
  Evidence: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj` completed successfully, but the embedded web build emitted `npm audit` output reporting 9 vulnerabilities (3 moderate, 3 high, 3 critical). This is outside the CLI runner-identity change and appears unrelated to the reviewed files.
  SuggestedAction: Triage dependency audit findings separately.
  Status: out-of-scope

## Verification Summary

- Read issue 364 via `mo issue show 364 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Read proposal, design, spec, tasks, progress, self-review, changed product files, adjacent runner refresh/readiness paths, and full-update verification tests.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter RuntimeConsistencyValidatorSpecs` -> passed 21/21 in 45 ms.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj` -> passed 660/660 in 230 ms.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj` -> passed 3812/3812, skipped 13, in 1 m 55 s.

<promise>FAIL</promise>
