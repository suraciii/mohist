# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: missing-obvious-guards
  Evidence: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Failure.cs` used `WorkflowControlRejectionException` from `Mohist.Server.Workflow.Domain` but the file only imported `Mohist.Server.Workflow.Domain.Definition`; this made the candidate fail compilation before review. Added the missing `using Mohist.Server.Workflow.Domain;` at line 2.
  Verification: `dotnet build Mohist.sln` passed; `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~RerunFromStage` passed 18 tests; `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter FullyQualifiedName~CliIssueRerunFromStageSpecs` passed 4 tests; `npm test` passed 786 tests with 23 skipped; `git diff --check` passed.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Failure.cs`
  Evidence: The live issue requires the target stage to be one the workflow run has already reached. The implementation uses the current stage index as the only reached boundary (`currentIdx` at line 144, `targetIdx > currentIdx` rejection at lines 156-163). After a successful `rerun-from-stage("plan")`, `CurrentStageId` is set back to `plan` at line 200 and later stages are replaced with fresh uninitialized `StageRun`s at lines 188-197, so a later stage that was reached earlier in the same run is now rejected as `stage_not_reached`. That does not satisfy the issue's lifetime wording, "already been reached by this workflow run". [disallowed: product behavior change and persisted/domain model judgment]
  SuggestedAction: Track the furthest/lifetime reached stage independently of `CurrentStageId` or otherwise preserve enough reached-stage evidence so stages reached before a backward rerun remain selectable until product requirements explicitly narrow the contract.
  Verification: Add a domain or grain regression that completes through `integrate`, calls `RerunFromStage("plan")`, then calls `RerunFromStage("integrate")` and verifies behavior matches the accepted lifetime contract.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: workflow timeline and event history
  Evidence: The acceptance criteria require invalidated old `StageRun` data not to be retained and the timeline not to show old attempt history. The aggregate does replace target and later `StageRun`s instead of appending them (`WorkflowRun.Failure.cs` lines 179-197), but it emits only `WorkflowRunResumed` and `StageStarted` (`WorkflowRun.Failure.cs` lines 203-206). Persisted workflow events are append-only (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs` lines 63-68), and the timeline/event routes return all issue/workflow events by time with no invalidation filtering (`packages/server/src/Mohist.Server/Api/WorkflowEventRoutes.cs` lines 26-36 and 42-45). Old `TaskStarted`/`TaskCompleted`/check events for invalidated attempts therefore remain visible through the event timeline. [disallowed: product behavior and traceability contract change]
  SuggestedAction: Define how timeline consumers distinguish invalidated control-state history from retained execution facts, then implement either an invalidation marker plus projection filtering or a timeline view based on the current `StageRun` snapshot for stage/task/check control state.
  Verification: Add an integration test that runs a stage, reruns from that stage or earlier, reads `/api/projects/{projectRef}/issues/{number}/events` or the canonical workflow timeline endpoint, and asserts invalidated attempt task/check history is not surfaced as active timeline history.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs`, `packages/server/src/Mohist.Server/Api/IssueRoutes.WorkflowControl.cs`
  Evidence: `WorkflowGrain.RerunFromStageAsync` catches the typed `WorkflowControlRejectionException` and rethrows an `InvalidOperationException` containing a string-delimited payload (`WorkflowGrain.cs` lines 222-225). The HTTP route then parses that string with a regex and a `|` delimiter (`IssueRoutes.WorkflowControl.cs` lines 251-270). The route only validates that `stage` is non-empty before passing user input to the grain (`IssueRoutes.WorkflowControl.cs` lines 137-141), so an unknown stage containing `|` can corrupt the returned message/details and makes the control contract depend on exception-message formatting. It also loses the typed rejection contract that the design and task expected at the grain boundary. [disallowed: public contract and architectural judgment]
  SuggestedAction: Preserve a typed, structured rejection across the grain/API boundary, or return a structured result object from the grain. Avoid parsing exception messages and avoid embedding user-controlled values in delimiter-sensitive protocol strings.
  Verification: Add an API regression for `POST /rerun-from-stage` with an unknown stage containing `|` and assert it returns `400` with `code = unknown_stage` and valid `details.eligibleStages`.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: rerun-from-stage API and runtime variable coverage
  Evidence: `packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/WorkflowRerunFromStageApiSpecs.cs` currently covers only empty stage (`lines 44-53`), no workflow run (`lines 58-67`), and unknown stage (`lines 72-88`). The task required HTTP integration coverage for 200 success, 400 never-reached with `eligibleStages`, and 409 active-work, but those route-level tests are absent. The issue also requires run-scoped runtime variables to survive and be readable by the new attempt; the new rerun-from-stage tests do not exercise `VariableBundle`, `setVars`, or effective variable reads for this operation. [disallowed: broader test implementation]
  SuggestedAction: Add the missing HTTP integration tests and a variable-preservation regression at the grain/integration level, especially because the route has custom exception parsing and the variables live outside the `WorkflowRun` aggregate.
  Verification: Run the new tests plus `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~RerunFromStage` and `npm test`.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: repository dependency audit
  Evidence: `dotnet build Mohist.sln` ran the web build path and npm reported 9 dependency vulnerabilities: 3 moderate, 3 high, and 3 critical. This appears unrelated to the rerun-from-stage implementation and was not changed by the candidate.
  SuggestedAction: Triage dependency audit separately from this workflow-control change.
  Status: out-of-scope

<promise>FAIL</promise>
