# Review Report

## Result: FAIL

## Repaired Items

_None._ No safe, local review repairs were made. The open findings affect resolver/API/UI behavior and contract semantics, so they are outside the allowed repair policy.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`; `packages/server/src/Mohist.Server/Api/IssueRoutes.Helpers.cs`; `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueWorkflowProfileApiConsistencySpecs.cs`; `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueWorkflowProfileConsistencySpecs.cs`
  Evidence: The post-build candidate returns `null` workflow profile ids from read surfaces when an existing issue's project has every system workflow disabled. `IssueQuerier.ToInfoAsync` passes `_effectiveProfileResolver.Resolve(...)` directly to `WorkflowProfileId` at `IssueQuerier.cs:872-898`, the synchronous read projection does the same at `IssueQuerier.cs:955-982`, and the workflow-profile endpoint returns that value as `profileId` at `IssueRoutes.Helpers.cs:151-173`. This conflicts with the approved task/design acceptance that read paths treat resolver `null` as `mohist/local` for display safety while creation blocks zero-enabled projects (`openspec/changes/issue-301/tasks.json:14`, `openspec/changes/issue-301/design.md:64-67`). The regression tests currently pin the opposite behavior by asserting JSON null / CLR null at `IssueWorkflowProfileApiConsistencySpecs.cs:766-809` and `IssueWorkflowProfileConsistencySpecs.cs:245-291`. [disallowed:product-behavior]
  SuggestedAction: Reconcile the contract. If the approved design/task is authoritative, coalesce resolver `null` to `IssueWorkflowProfiles.LocalId` in read projections and update the null-asserting tests. If `null` is now the intended product behavior, update the OpenSpec design/tasks/specs so the candidate contract is explicit and reviewable.
  Verification: Source inspection plus focused server tests. `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~ProjectWorkflowProfileDisabledSpecs|FullyQualifiedName~EffectiveWorkflowProfileResolverSpecs|FullyQualifiedName~IssueWorkflowProfileApiConsistencySpecs|FullyQualifiedName~IssueWorkflowProfileConsistencySpecs|FullyQualifiedName~WorkflowProfileManagerSpecs|FullyQualifiedName~WorkflowSessionSpecs|FullyQualifiedName~CreateIssueSkillContentSpecs|FullyQualifiedName~DatabaseInitializationSpecs"` passed 144 tests, but the passing tests currently assert the conflicting null read-surface behavior.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/entities/settings/api/queries.ts`; `packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx`; `packages/web/src/widgets/issue-workflow/ui/WorkflowProfileControl.tsx`; `packages/web/src/entities/settings/api/queries.test.ts`; `packages/web/src/pages/settings/ui/ProjectDefaultWorkflowControl.tsx`
  Evidence: `useEffectiveDefaultWorkflowProfile` treats any configured default that is not in `disabledWorkflowProfileIds` as the effective project workflow without checking that it is present in the current project-scoped enabled workflow catalog (`queries.ts:374-383`). The Settings control separately detects a configured default missing from the system catalog and shows an amber warning (`ProjectDefaultWorkflowControl.tsx:104-112`), but the shared effective-default hook can still return that unavailable id to Create Issue (`CreateIssueDialog.tsx:188-189`) and the issue workflow selector (`WorkflowProfileControl.tsx:25-30`). `WorkflowProfileControl` then appends the absent default back into its selector at `WorkflowProfileControl.tsx:40-50`, making an unavailable profile look selectable. Existing coverage pins this behavior: `queries.test.ts:261-270` returns `mohist/github-pr` as a project default even when the enabled profile list only contains `mohist/local`, and `WorkflowProfileControl.test.tsx:236-253` expects an absent default to be added to the selector. This undermines the issue requirement that defaults fall through to enabled profiles when the configured default is not usable. [disallowed:product-behavior]
  SuggestedAction: Resolve a configured default as an effective workflow profile only when it is present in the enabled project-scoped workflow list, or explicitly separate project-template defaults from system workflow-profile defaults in the API/UI model. Create Issue and issue workflow controls should not present an absent system profile as selectable; they should fall through to the first enabled profile or `source: 'none'` consistently with the Settings warning.
  Verification: Source inspection plus focused web tests. `npm run typecheck -w packages/web` passed. `npm run test:run -w packages/web -- WorkflowProfilesSection ProjectDefaultWorkflowControl queries.test CreateIssueDialog WorkflowProfileControl settings-search-registry` passed 14 files / 218 tests, but the passing tests include expectations for the stale-default behavior above.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: CLI degraded discovery path
  Evidence: In `mo workflow list --described` with no explicit or active project, `MohistCliCommands.Workflow.cs:50-55` calls `ResolveProjectIdAsync`, which emits the generic no-active-project guidance from `MohistCliApi.cs:963-966`, and then emits the degraded fallback note. This is not a contract break because the command still falls back to unfiltered discovery, but the stderr output is noisier than the acceptance wording implies.
  SuggestedAction: Consider using the silent `TryReadActiveProjectIdAsync` path for degraded discovery, or add a test that intentionally locks in the two-line stderr behavior.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: Settings Search UI integration
  Evidence: `WORKFLOW_DESCRIPTORS` is populated and unit-covered, but the reviewed tests mostly verify descriptor contents and target ids. They do not exercise the full user path of searching for "workflow", selecting a result, navigating to the Workflows tab, and focusing `#workflow-profiles-section` or `#project-default-workflow`.
  SuggestedAction: Add an integration-style Settings Search test for selecting a workflow result and verifying tab navigation plus focus target behavior.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: workflow artifacts
  Evidence: `openspec/changes/issue-301/` contains proposal, design, tasks, specs, self-review, progress, and review artifacts. Per the candidate boundary, these are workflow context/evidence, not product deliverables by themselves, and their presence is expected during this stage.
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-6]
  Severity: warning
  Scope: dependency audit output
  Evidence: The focused server test command invoked the web build and reported existing npm audit output: 9 vulnerabilities (3 moderate, 3 high, 3 critical). The issue-301 diff does not add dependencies, so this is not attributed to the current change.
  SuggestedAction: Track dependency audit remediation separately if not already covered.
  Status: pre-existing

## Verification Summary

- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~ProjectWorkflowProfileDisabledSpecs|FullyQualifiedName~EffectiveWorkflowProfileResolverSpecs|FullyQualifiedName~IssueWorkflowProfileApiConsistencySpecs|FullyQualifiedName~IssueWorkflowProfileConsistencySpecs|FullyQualifiedName~WorkflowProfileManagerSpecs|FullyQualifiedName~WorkflowSessionSpecs|FullyQualifiedName~CreateIssueSkillContentSpecs|FullyQualifiedName~DatabaseInitializationSpecs"` passed 144 tests.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter FullyQualifiedName~CliWorkflowListSpecs` passed 10 tests.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web -- WorkflowProfilesSection ProjectDefaultWorkflowControl queries.test CreateIssueDialog WorkflowProfileControl settings-search-registry` passed 14 files / 218 tests.

<promise>FAIL</promise>
