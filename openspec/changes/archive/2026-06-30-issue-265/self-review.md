# Self Review Report

## Result: PASS

Scope: reviewed `proposal.md`, `design.md`, `tasks.json`, `specs/workflow-stage-rerun/spec.md` against issue #265, and verified the codebase claims in `design.md` against the actual source (`WorkflowRun.Failure.cs`, `StageRun.cs`, `WorkflowRun.Lifecycle.cs`, `WorkflowRun.Stage.cs`, `WorkflowRun.cs`, `WorkflowGrain.cs`, `IWorkflowGrain.cs`, `IssueRoutes.WorkflowControl.cs`, `WorkflowSessionContextExhaustedException.cs`, `MohistCliCommands.Issue.cs`, and the referenced test files).

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: feasibility
  Evidence: T-002 acceptance criterion referenced `` `npm run typecheck -w packages/cli` if applicable `` as the CLI verification command. The `mo` CLI is a .NET project (`packages/cli/Mohist.Cli`); only `packages/web` and `packages/runner` are npm workspaces (see root `package.json`), so that script does not exist. The `(if applicable)` hedge made it self-correcting, but it was imprecise about how CLI tests are actually run (`npm test` → `dotnet test Mohist.sln`).
  Verification: Replaced the criterion with the accurate command: `` `dotnet build Mohist.sln` succeeds with TreatWarningsAsErrors and CLI tests pass (CLI is a .NET project covered by `npm test` / `dotnet test Mohist.sln`; there is no `packages/cli` npm workspace) ``. Confirmed against root `package.json` (`test` = `dotnet test Mohist.sln ...`) and `AGENTS.md` (cli is .NET).
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: `proposal.md:16` illustrates the endpoint as `POST /api/issues/{number}/rerun-from-stage` (with the `如`/"e.g." qualifier), while the normative `spec.md` and `design.md` (D6) and `tasks.json` all use the fully-qualified `POST /api/projects/{projectRef}/issues/{number}/rerun-from-stage`, matching the actual sibling routes mapped under the project-scoped group in `IssueRoutes.WorkflowControl.cs`. The proposal's `Impact` section (line 35) already gives the route-relative form `/{number}/rerun-from-stage`, so the doc is internally consistent in using shorthand and the illustrative intent is clear.
  SuggestedAction: Optionally normalize `proposal.md:16` to the fully-qualified path for crispness; not required since the spec/design/tasks are authoritative and consistent.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: `spec.md` ("Execution facts ... not rolled back") and T-001 acceptance criterion require a "domain test" asserting runtime variables survive the operation and remain readable by the target's new attempt. In the codebase, `setVars` products accumulate on the issue's workflow-profile rows (`VariableBundle` / `StageVariables` in `IssueWorkflowProfileManager` / `IssueWorkflowProfileStorageIntegrity`), NOT on the `WorkflowRun` aggregate (`WorkflowRun.cs` has no `Variables` field). So `RerunFromStage` trivially preserves them (it only mutates `Stages`/`CurrentStageId`/`Failure`/`Status`), but a pure domain-level test cannot assert "variables remain" without the variable store. The operational requirement is satisfied by design; only the test placement wording is slightly loose.
  SuggestedAction: Implementer should treat the variables-preservation assertion as a grain/integration-level check (or assert indirectly that the domain method does not touch profile/variable state) rather than a strict `WorkflowRun`-only domain test.
  Status: follow-up

## Notes

- Alignment: all 11 issue Acceptance Criteria and every Non-Goal map to spec requirements and tasks; no issue requirement is missing or misinterpreted.
- Completeness: every spec requirement (`range invalidation`, `reached stage`, `active-work block`, `stage locks`, `execution facts not rolled back`, `invalidated data not retained`, `HTTP endpoint`, `CLI command`, `retry/rerun unchanged`) is covered by a task; edge cases (unknown stage, never-reached stage, active work in a later stage, lock before target, target = last stage) are addressed in scenarios / acceptance criteria.
- Consistency: design decisions D1–D7 match the spec scenarios and the existing `Rerun()`/`Retry()` patterns; spec capability name `workflow-stage-rerun` matches the spec directory; task spec anchors resolve to existing spec headings; task output paths reference real files.
- Feasibility: verified every codebase anchor in `design.md` exists — `Rerun()` at `WorkflowRun.Failure.cs:121`, `Retry()` at `:28`, `InitializeStage`/`Advance` in `WorkflowRun.Stage.cs`, `InitializeFreshStagesAsync` / `ReleaseStageLocksAsync` / `GetSequentialLockResourceAsync` in `WorkflowGrain.cs`, `BuildAction`/`BuildReject` in `MohistCliCommands.Issue.cs`, the `[Theory]` sets in `IssueArchivedDetailApiSpecs.cs`, and the CLI project-ref `[Theory]` in `IssueCliRemainingProjectRefSpecs.cs`. The domain precedent for the typed exception (`WorkflowSessionContextExhaustedException`) exists, and the Domain layer already references `Mohist.Server.Workflow.Grains` (`WorkItem.cs:3`), so the exception placement note in T-001 is feasible either way. The emitted `[WorkflowRunResumed, StageStarted(target)]` correctly drives `CommitAsync → InitializeFreshStagesAsync` to init the target in the same commit and lazily init later stages on advance, satisfying the "later stages reinitialize against the current template" scenario. Task granularity is appropriate: two cohesive vertical slices (server end-to-end, CLI), tests embedded in each — no over-fine tasks, no standalone "add test"/"register DI" tasks.
- Dependency completeness: T-001 has no dependencies (first task); T-002 `dependsOn: ["T-001"]` points to an existing lower-priority task; no cycles.

<promise>PASS</promise>
