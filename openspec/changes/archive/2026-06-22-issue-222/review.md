# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: none
  Evidence: No small, local, low-risk repair was made during this review. The post-repair candidate snapshot was reviewed as-is.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~AgentJobGrainSpecs|FullyQualifiedName~RunnerDefinitionStateSpecs|FullyQualifiedName~RunnerSlotsApiSpecs|FullyQualifiedName~RunnerGlobalizationSpecs|FullyQualifiedName~RunnerStatusProjectionSpecs|FullyQualifiedName~IssueCreationSpecs.StartWorkflow_WithProjectContext_DispatchesProjectVariables|FullyQualifiedName~IssueWorkflowProductLoopSpecs.ProjectVariablesEdit_PropagatesToIssueCreatedWithPriorProjectConfig"` passed: 58/58.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: info
  Scope: candidate snapshot
  Evidence: No unresolved blocking findings remain. Runner definition state is persisted via `RunnerDefinitionStore.GetOrInitAsync` / `UpdateSlotsAsync`, DB mapping enforces positive `Slots`, `RunnerGrain` sources capacity from persisted `_slots`, register/heartbeat reported `MaxWorkflowSlots` is non-authoritative, runner registry usage is global, `PATCH /api/runner/{runnerId}` validates positive slots and updates through the grain, and focused tests cover workflow slots, agent-job capacity, persisted slots, globalization, status projection, PATCH behavior, and prior issue workflow regressions.
  SuggestedAction: None.
  Verification: Focused test command above passed.
  Status: resolved

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Orleans/GrainKey.cs:11`
  Evidence: The old project-scoped `GrainKey.RunnerRegistry(string projectId)` helper remains but is marked obsolete with "Runner registries are global only; use RunnerRegistryKeys.Global." No production call site uses it, so this is not a current product defect.
  SuggestedAction: Remove the obsolete helper in a later cleanup when cross-branch merge risk is low.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: workflow artifacts under `openspec/changes/issue-222/`
  Evidence: `proposal.md`, `design.md`, `tasks.json`, delta specs, `self-review.md`, and this `review.md` are expected workflow artifacts during Plan/Build/Check/Integrate and do not count as product deliverables or failures by themselves.
  SuggestedAction: Keep artifacts until the workflow reaches integration/archive.
  Status: out-of-scope

- [ID: item-5]
  Severity: warning
  Scope: dependency audit output
  Evidence: The verification command emitted existing npm audit output for 9 vulnerabilities during the web build step invoked by the .NET test project. This is not introduced by the runner slots change and was not part of the reviewed product delta.
  SuggestedAction: Track dependency audit remediation separately.
  Status: pre-existing

<promise>PASS</promise>
