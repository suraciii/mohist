# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `packages/server/src/Mohist.Server/Api/WorkflowRunDetailDto.cs:32` had an extra blank line at EOF; removed it so the post-repair working tree passes whitespace validation.
  Verification: `git diff --check` passed with no output.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: comment-consistency
  Evidence: `packages/server/src/Mohist.Server/Api/WorkflowRunDetailDto.cs:9-13` described `issueRef` as an in-progress issue binding, but the implemented contract and `WorkflowRunDetailApiSpecs.Get_WhenIssueRowIsTerminal_IssueRefStillCarriesCorrelationContext` are status-independent. Updated the XML comment to say associated issue / no issue row.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~WorkflowRunDetailApiSpecs"` passed: 11 passed, 0 failed. `git diff --check` passed with no output.
  Status: resolved

## Blocking Items

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Api/WorkflowRoutes.WorkflowControl.cs`
  Evidence: The new run-scoped rerun endpoints do not preserve the existing rerun recovery path. The issue-scoped endpoints catch workflow-run state deserialization corruption and recover by calling `StartWorkAsync` (`packages/server/src/Mohist.Server/Api/IssueRoutes.WorkflowControl.cs:110-119` and `:138-156`). The new direct endpoints call `RerunAsync` / `RerunFromStageAsync` without that catch (`packages/server/src/Mohist.Server/Api/WorkflowRoutes.WorkflowControl.cs:76-84` and `:87-107`). That violates the issue requirement that workflowRunId control commands have the same behavior as issue shortcuts and the review instruction to inspect adjacent retry/recovery paths. [disallowed:product-behavior-change]
  SuggestedAction: Move the state-corruption recovery into a shared helper used by both addressing axes, or explicitly decide that direct workflow rerun should not recover and update the issue/specs/tests accordingly. Add a regression test where the run state is corrupted and both `/api/projects/{project}/issues/{number}/rerun[-from-stage]` and `/api/workflow-runs/{id}/rerun[-from-stage]` produce the same recovery outcome.
  Verification: Existing targeted tests pass but do not cover this case: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~WorkflowRunControlApiSpecs|FullyQualifiedName~WorkflowRunDetailApiSpecs|FullyQualifiedName~WorkflowRetrySessionHealthGuardSpecs"` passed: 48 passed, 0 failed. Add the corruption-parity test above to verify the fix.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `specs/cli-workflow-list/spec.md`
  Evidence: The updated product spec still defines JSON output as `mo project workflow profile list --json` (`specs/cli-workflow-list/spec.md:14` and `:49`) and calls it the `--json` output format (`:46`), but the implemented and documented command exposes the shared `--output/-o` option only (`packages/cli/Mohist.Cli/MohistCliCommands.cs:61-66`, `packages/cli/Mohist.Cli/MohistCliCommands.ProjectWorkflow.cs:33-38`, `docs/cli-reference.md:315`). The relocated profile tests also exercise default JSON output or `-o json`, not `--json`, so this stale contract is untested. Consumers following the spec will run an unsupported flag. [disallowed:public-contract-change]
  SuggestedAction: Either update `specs/cli-workflow-list/spec.md` to the actual `-o/--output json` contract and current response fields, or intentionally add a `--json` compatibility flag and cover it in CLI tests.
  Verification: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~CliWorkflowControlSpecs|FullyQualifiedName~CliWorkflowReads|FullyQualifiedName~CliProjectWorkflowProfileSpecs|FullyQualifiedName~CliReferenceDocsSpecs"` passed: 83 passed, 0 failed, but no test asserts `--json` behavior. Add a spec/CLI alignment test after deciding the contract.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: warning
  Scope: full-suite verification
  Evidence: A broad `npm test -- packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Api/WorkflowRunControlApiSpecs.cs packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/WorkflowRetrySessionHealthGuardSpecs.cs` invocation actually ran the full .NET suite, reported an unrelated `WorkflowSessionSpecs.GivenRunnerReportsAcpSessionEvents_WhenSessionIsQueried_ThenEventsAreSavedInSessionOrder` 404 failure, and then timed out at 120s. The changed targeted server and CLI test filters passed, so this does not add another current-change blocker.
  SuggestedAction: Investigate the existing session-query 404 separately, and use direct `dotnet test --filter ...` commands for targeted C# verification.
  Status: out-of-scope

<promise>FAIL</promise>
