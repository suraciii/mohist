# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: cleanup
  Evidence: `MohistLocalIssueWorkflowProfile.cs` retained an unused `using Mohist.Server.Workflow.Domain.Definition;` after the `ResolveDescription()` private method was removed. The type `WorkflowDefinition` is no longer referenced directly in this file (only transitively through `MohistWorkflow.Definition`).
  Verification: `dotnet build Mohist.sln --nologo -v q` passes with 0 warnings, 0 errors.
  Status: resolved (import removed)

## Blocking Items

_(none)_

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistLocalWorkflowProfileSpecs.cs:1132`
  Evidence: The test `DefaultIssueWorkflowProfile_DescriptionFallsBack_WhenYamlHasNoDescription` manually replicates the fallback logic in a local variable (`var fallbackDescription = string.IsNullOrWhiteSpace(yamlWithoutDescription.Description) ? fallback : yamlWithoutDescription.Description!;`) instead of calling the production code path `MohistWorkflow.ResolveDescription(yamlWithoutDescription)`. The test name says it tests the profile's fallback, but the test body never touches the profile or `MohistWorkflow.ResolveDescription`. The fallback behavior is verified elsewhere (through `ResolveDescription` assertions in other tests), so this test is a weak duplicate, not a missing coverage gap.
  SuggestedAction: Replace the inline logic with `MohistWorkflow.ResolveDescription(yamlWithoutDescription)` to make the test test the production code path it claims to test.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistLocalWorkflowProfileSpecs.cs:1126`
  Evidence: The test `DefaultIssueWorkflowProfile_DescriptionSourcesFromWorkflowYaml` asserts both `MohistWorkflow.ResolveDescription(MohistWorkflow.Definition)` and `MohistWorkflow.Definition.Description!.TrimEnd()`. The second assertion is redundant since `ResolveDescription` already applies `.TrimEnd()`. Not wrong, just unnecessary.
  SuggestedAction: Remove the redundant assertion to keep the test lean.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/skill-data/mohist-create-issue/SKILL.md:49`
  Evidence: The phrase "Do not look for suitability tags. The natural-language description exists to tell a human reader what the profile does; it is not a scoring input for the agent." uses the word "tags" rather than the prohibited phrase `suitable_for`. This is semantically equivalent to saying "do not look for tags" — a general prohibition. Since the spec only requires no mention of `suitable_for`, this is compliant but could be tightened to avoid confusion with other tag concepts (`label` tags, `IssueTemplate` tags, etc.).
  SuggestedAction: Consider making the prohibition more specific: "Do not score profiles against content keywords or look for structured tag-like metadata."
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/IssueTemplateRegistry.cs:273`
  Evidence: The `IssueTemplate.SuitableFor` property and the `IssueTemplateRegistry` model share the name `SuitableFor` with the removed workflow-profile concept, but are a different domain (issue templates, not workflow profiles). These remain and are explicitly out of scope per the design document.
  SuggestedAction: None. Out of scope. Verified that no workflow-profile code references these.
  Status: out-of-scope

- [ID: item-6]
  Severity: info
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/IssueWorkflowProfileApiSpecs.cs`
  Evidence: 13 integration tests in this file are skipped with `Fact(Skip = "Integration test host cannot boot due to pre-existing pending EF migration unrelated to this change.")`. These skips pre-date this change and are confirmed in `progress.txt` and `self-review.md`.
  SuggestedAction: None. Pre-existing infrastructure issue.
  Status: pre-existing

<promise>PASS</promise>
