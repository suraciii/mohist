# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: inconsistency between domain `Create` default and grain `CreateAsync` default
  Evidence: `Domain.Issue.Create` defaults `isDraft = true` (packages/server/src/Mohist.Server/Issue/Domain/Issue.Transitions.cs:16) while `IssueGrain.CreateAsync` and `IIssueGrain.CreateAsync` default the grain-level `isDraft = false` (packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:332, packages/server/src/Mohist.Server/Issue/Grains/IIssueGrain.cs:9). The HTTP route defends the wire contract with `req.IsDraft ?? true` (packages/server/src/Mohist.Server/Api/IssueRoutes.Crud.cs:58), so all issue-142 acceptance criteria and end-to-end tests pass today. The grain's `false` default is a latent foot-gun for any future in-process caller of `IssueGrain.CreateAsync`, since omitting `isDraft` would silently create a ready (non-draft) issue, contradicting the issue's "New issues default to draft" rule. Aligning the grain default to `true` requires also passing `isDraft: false` in two direct-grain calls in `IssueRepositoryResolutionRegressionSpecs.cs` (lines 545, 725) that don't go through the route and immediately call `StartWorkAsync`. [disallowed:product-behavior change]
  SuggestedAction: Either change `IssueGrain.CreateAsync`/`IIssueGrain.CreateAsync` to default `isDraft = true` and add `isDraft: false` to the two regression test sites (preferred — matches the domain's "new issues are draft" invariant), or document the intentional difference.
  Verification: After alignment, `dotnet test ... --filter "IssueStartReadinessDomainSpecs|IssueStartReadinessApiSpecs|IssueCliStartReadinessSpecs|IssueCreationSpecs|IssueApiSpecs|IssueRepositoryResolutionRegressionSpecs|IssueSessionApiSpecs"` should still pass.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: IssueQuerier.ComputeBlockerForReadModel duplicates Issue.StartBlocker semantics
  Evidence: `IssueQuerier.ComputeBlockerForReadModel` (packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:457-467) is a static re-implementation of `Issue.StartBlocker` (packages/server/src/Mohist.Server/Issue/Domain/Issue.Transitions.cs:88-100). Both encode the same rule (Draft → WaitingFor(prereq) → null). The querier cannot call `Issue.StartBlocker` because it works against the deserialized `IssueReadModel`, but the duplication creates drift risk if the precedence rule changes.
  SuggestedAction: Either expose a static `Issue.ComputeStartBlocker(bool isDraft, IReadOnlyList<int> prereqs, IReadOnlySet<int> undelivered)` and reuse it from both call sites, or document that the two implementations must be kept in sync.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: WaitingForBlocker title/stage/status dropped on start refusal
  Evidence: `IssueRoutes.Lifecycle.cs:34` uses the single-argument overload `IssueStartBlockerDto.FromDomain(ex.Blocker)`. For a `WaitingFor(prereq)` blocker this constructs `IssuePrerequisiteRefDto` with only `Number` set, leaving `Title`/`Stage`/`Status` empty (packages/server/src/Mohist.Server/Issue/Services/IssueInfo.cs:98-110). The list/detail paths use the summaries-aware overload and include title/stage/status, so the refusal payload is data-poorer than the otherwise-identical blocker shape.
  SuggestedAction: Plumb the prerequisite summaries into the start route (or the grain) and call `IssueStartBlockerDto.FromDomain(blocker, summariesByNumber)` so the start-refusal `blocker` carries the same data as the list/detail read path.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: migration `20260618093938_SyncAgentSessionProjection` bundled with the issue 142 changeset
  Evidence: `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260618093938_SyncAgentSessionProjection.cs` (and its Designer + the `MohistDbContextModelSnapshot.cs` delta) drops `AgentSessions.CompletedAt`/`UpdatedAt` columns. The commit message of T-002 documents it as "left behind by a prior projection change" and unrelated to IsDraft / start-readiness. Bundling an unrelated schema cleanup into the issue 142 cutover widens the blast radius of a rollback and obscures the migration story for issue 142 (which the design claims needs no schema change).
  SuggestedAction: Move the AgentSession column drop into its own change/PR with its own migration timestamp; revert the migration files and the snapshot delta from the issue 142 cutover.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: CanStart/Blocker do not reflect execution status (spec compliance vs. consumer expectations)
  Evidence: `CanStart` is derived purely from `IsDraft` + prerequisites per spec (`issue-start-readiness/spec.md` line 33), and `IssueQuerier.ComputeBlockerForReadModel` enforces this. As a result, a `Done`, `Cancelled`, or `InProgress` issue with all prerequisites delivered returns `canStart: true, blocker: null`. `Issue.Start` would still refuse it with `InvalidOperationException`, but a thin client that only reads `canStart` could misinterpret the field as an authorization flag. `IssueDetailPage.tsx` and the CLI `FormatIssueState` already guard with `isBacklog`/`status` checks, but external consumers reading the API may not.
  SuggestedAction: Consider extending the blocker sum with an execution-status case (e.g. `AlreadyRunning(WorkflowRunId)` / `Terminal(IssueStatus)`), or document that `canStart` is a draft/prereq-derived query and that start authority also requires checking `status`/`health`. Today's behavior matches the spec but leaves the inconsistency unstated.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: SetDraft refusal semantics on started issues not covered by spec
  Evidence: `Issue.Transitions.cs:65-74` throws `InvalidOperationException` when `SetDraft` is invoked on an `InProgress`/`Done`/`Cancelled` issue. This is asserted by `IssueStartReadinessDomainSpecs.SetDraft_AfterStart_Throws`, but neither the issue acceptance criteria nor the `issue-start-readiness` spec scenarios state the refusal contract (message text, exception type) for the API surface. The PATCH `/api/issues/:number` route maps `InvalidOperationException` to `409 Conflict` with a plain message (packages/server/src/Mohist.Server/Api/IssueRoutes.Crud.cs:92-95), giving no structured readiness payload on this refusal.
  SuggestedAction: Decide whether `SetDraft` on a started issue should be a 400-class response with `canStart`/`blocker` semantics, and add an acceptance criterion + scenario to `issue-start-readiness/spec.md` so the API surface is unambiguous.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `Issue.IsDraft` initializer asymmetry for new issues via grain vs. route (related to item-1)
  Evidence: When an HTTP client sends `POST /api/issues` with `isDraft: true`, the route forwards `req.IsDraft` to `grain.CreateAsync(..., isDraft: true)` (IssueRoutes.Crud.cs:49-58). When the same client sends `isDraft: false`, the route forwards `false`. When the client omits the field, the route falls back to `req.IsDraft ?? true` (draft). The grain's `CreateAsync` default is `false`, so the route's `?? true` is the only thing protecting the wire contract from the underlying inconsistency. If item-1 is fixed by aligning the grain default to `true`, the route's `?? true` becomes redundant; conversely, the route's defensive default hides the grain-side foot-gun from any direct in-process caller.
  SuggestedAction: Resolve item-1 and remove `?? true` from the route, or document why the route double-applies the default.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-8]
  Severity: warning
  Scope: pre-existing test failures in unrelated suites
  Evidence: `dotnet test packages/server/tests/Mohist.Server.Tests` reports 59 failures. The issue-142-specific suites (`IssueStartReadinessDomainSpecs`, `IssueStartReadinessApiSpecs`, `IssueCliStartReadinessSpecs`, `IssueCreationSpecs`, `IssueApiSpecs`, `IssueRepositoryResolutionRegressionSpecs`, `IssueSessionApiSpecs`) all pass (20 + 11 + 22 + 15 + 13 + 24 + 3 = 108 tests). The failures are concentrated in:
  - `IssueCliRemainingProjectRefSpecs`/`ProjectCliRepositorySpecs`/`IssueCliBodyInputSpecs`/`IssueCliProjectRefAndOutputSpecs` — `InvalidOperationException : No service for type 'Mohist.Cli.IServiceInstaller' has been registered` at `MohistCliCommands.Build(api, provider)` (MohistCliCommands.cs:16, MohistCliCommands.Server.cs:12). The CLI host registers `IServiceInstaller` (MohistCliCommands.cs:117), but these tests build the command tree directly without the DI container.
  - `Architecture.DomainInternalLayers_ShouldBeFreeOfCycles` — Sessions slice cycle (`AgentSessionResolver → IAgentSessionGrain/AgentSessionInfo`, `AgentSessionGrain → TranscriptAccumulator/RuntimeEventEnvelope/AgentSessionJsonHelper`).
  - `IssueWorkflowProductLoopSpecs.{ProjectStageVariablesPatch_OverridesPersistedWorkflowStageAgent, ProjectVariablesPatch_AppliesToNextTaskDispatch, IssueStart_RunnerCompletesWorkflow_IssueBecomesDone}`, `IssueWorkflowProfileApiSpecs.SaveWorkflowProfileYaml_SynchronizesActiveRunProfile_AndPreservesInitializedStageWork`, `ActivityWaitingApiSpecs.*`, `WorkflowVariableSpecs.{MohistWorkflowUsesExpressionInputs, MohistWorkflowUsesCoreActionsForGenericChecks}` — runner/harness flakiness ("Runner '...' has no work" / poll timeouts), reproducible on the baseline `2c4889bc`.
  These all reproduce on the baseline (verified by `git checkout 2c4889bc -- ...` and re-running) and none of the modified files in issue 142 alter their call sites.
  SuggestedAction: Fix the CLI command-tree tests by registering `IServiceInstaller` in the test setup, and resolve the Sessions slice cycle and runner-poll flakiness in their respective changes.
  Status: pre-existing

- [ID: item-9]
  Severity: info
  Scope: pre-existing CLI help-text inconsistency not addressed by issue 142
  Evidence: The CLI's `issue create --help` documents `--body`/`--body-file`/`--body-stdin` as mutually exclusive with messages that mention only one option at a time (MohistCliCommands.Issue.cs:109-111, 230-232). `IssueCliBodyInputSpecs` exercises the `--help` output for all three options. These tests fail for the same DI-registration reason described in item-8 and are unrelated to the draft/readiness change.
  SuggestedAction: Update the option descriptions to reference the full mutual-exclusion set when fixing the CLI command-tree tests in item-8.
  Status: pre-existing

- [ID: item-10]
  Severity: info
  Scope: pre-existing circular prerequisite returns 404 instead of 400
  Evidence: `IssueRoutes.Prerequisites.cs:25-26` maps every `IssuePrerequisiteResult.Success == false` to `ApiResults.NotFound(result.Message)`, so a self-reference is reported as 404 (tests confirm at `IssueStartReadinessApiSpecs.CircularPrerequisiteDeclaration_StillRejects_AndReturnsReadinessFields` and pre-existing tests). The `http-api` spec scenario "Reject circular start prerequisite declaration" requires a 400-class response with reason `circular-prerequisite`. The new test only asserts the readiness fields and ignores the status code.
  SuggestedAction: Add a 400-mapping branch for `result.Code == "circular_prerequisite"` and have the test assert `BadRequest`; align the spec's expected status code with the existing 404 if 404 is intentional.
  Status: pre-existing

- [ID: item-11]
  Severity: info
  Scope: pre-existing web test failures
  Evidence: `packages/web` `vitest run` reports 13 failures across 5 files: `tests/canonical-event-types.test.ts`, `src/widgets/app-shell/ui/Header.test.tsx`, `src/pages/epics/ui/EpicListPage.test.tsx`, `tests/useCoderSessions.test.tsx`, `tests/live-task-cloud-event.test.tsx`. None of these test files were modified by issue 142 (verified by `git diff 2c4889bc..HEAD --name-only -- packages/web/tests/...`); the issue-142 web tests (`IssueCard.test.tsx`, `IssueDetailPage.readiness.test.tsx`, `EpicDetailPage.test.tsx`, `WorkflowArtifacts.test.tsx`, `WorkflowTaskArtifact.test.tsx`, `WorkflowView.test.tsx`, `kanban-grouping.test.ts`, `kanban-board-query.test.tsx`) all pass (157 tests across 12 files).
  SuggestedAction: Investigate the Header/EpicListPage/useCoderSessions/canonical-event-types/live-task-cloud-event failures independently; they predate issue 142.
  Status: pre-existing

<promise>PASS</promise>