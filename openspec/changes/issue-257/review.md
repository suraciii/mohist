# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs
  Evidence: `WorkflowProfileManager.LoadTemplateAsync` no longer resolves project default custom templates. Before this change, the documented template precedence included `project_workflow_profile.DefaultTemplateId` as a template reference fallback (`packages/server/src/Mohist.Server/Workflow/Services/ResolvedTemplate.cs:9`). The current implementation jumps from issue custom/source overrides directly to the effective profile id and only calls `ProjectWorkflowProfileManager.GetSystemTemplateDefinition(effectiveProfileId)` (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs:87`). Because `EffectiveWorkflowProfileResolver` only treats ids registered in the system issue profile registry as valid, a project default like `default-tmpl` is ignored and startup falls back to `mohist/default`. The updated test now codifies the regression by naming `LoadTemplate_ProjectDefaultCustomTemplate_UsesEffectiveSystemProfile` and expecting `system-template:mohist/default` even when `projectDefaultTemplateId: "default-tmpl"` points at a 5-stage project template (`packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Querier/WorkflowProfileManagerSpecs.cs:120`). This breaks existing project default workflow-template behavior and the issue acceptance criterion that absent issue-level selection inherits the project default before system default. [disallowed:product-behavior-change]
  SuggestedAction: Restore project default custom-template resolution for issues without an explicit issue-level `WorkflowProfileId`, while still ensuring an explicit issue selection such as `mohist/pr` wins over the project default. Add/restore tests for both cases: no issue selection uses a custom project default template, and explicit issue PR selection overrides a project default.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~WorkflowProfileManagerSpecs"` and an integration startup test showing project default custom templates still run when no issue-level selection exists.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: CodeGraph
  Evidence: CodeGraph is not initialized in this workspace, so the review used direct diff, grep, and file reads.
  SuggestedAction: Optionally initialize CodeGraph for future large cross-cutting reviews.
  Status: out-of-scope

- [ID: item-3]
  Severity: info
  Scope: verification
  Evidence: The previous repair run reported passing targeted checks: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~WorkflowProfileManagerSpecs"`, `npm run test:run -w packages/web -- IssueCard.test.tsx`, and `npm test -w packages/runner`. These checks do not catch the project-default custom-template regression because the server test expectation was changed to the incorrect behavior.
  SuggestedAction: Keep those checks, but add the missing project-default custom-template assertion described in item-1.
  Status: out-of-scope

<promise>FAIL</promise>
