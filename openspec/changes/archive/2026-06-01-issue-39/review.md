# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/tests/IssueWorkflowProfileEditor.test.tsx`
  Evidence: The required UI coverage for save progress, validation errors, and dirty-state behavior is not currently runnable in this repo. Running `npm exec vitest run packages/web/tests/IssueWorkflowProfileEditor.test.tsx` fails before test collection with `Vitest failed to find the current suite` from `packages/web/tests/setup.ts:5`, and `npm test -- --run packages/web/tests/IssueWorkflowProfileEditor.test.tsx` also fails because the root `test` script dispatches to `dotnet test` instead of Vitest. Issue 39 explicitly requires UI behavior coverage, and the verdict rules do not allow PASS when relevant tests are not meaningfully passing.
  SuggestedAction: Fix the web test invocation/setup so the new editor tests execute under the repo's supported Vitest configuration, then rerun them and capture the passing command output.
  Verification: `npm exec vitest run packages/web/tests/IssueWorkflowProfileEditor.test.tsx`
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.tsx`
  Evidence: Validation error classification is inferred from `Error.message` substring matching (`yaml_syntax` or `yaml`) rather than consuming a structured API error payload. This works only if the client keeps formatting server errors exactly as expected, which is a brittle coupling for an editor that is supposed to surface clear syntax-vs-shape feedback.
  SuggestedAction: Parse the API error code from the client error object and render that directly, instead of deriving error type from free-form message text.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Api/IssueRoutes.cs`, `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/IssueWorkflowProfileApiSpecs.cs`
  Evidence: Server-side acceptance criteria are well covered in the current snapshot. The issue-scoped GET/PUT endpoints exist at `/api/issues/{number}/workflow/profile/yaml` (`IssueRoutes.cs:576-645`), invalid YAML syntax and invalid workflow shape are mapped separately to `yaml_syntax` and `workflow_shape` (`IssueRoutes.cs:612-624`), normalized YAML and refresh metadata are returned (`IssueRoutes.cs:638-645`), issue profile updates propagate to active workflow runs via `UpdateProfileDefinitionAsync` without stage regeneration (`IssueGrain.cs:403-409`, `WorkflowGrain.cs:461-466`), and the targeted server spec suite passes: `dotnet test packages/server/tests/Mohist.Server.Tests --filter IssueWorkflowProfileApiSpecs`.
  SuggestedAction: None required for this change.
  Status: out-of-scope

<promise>FAIL</promise>
