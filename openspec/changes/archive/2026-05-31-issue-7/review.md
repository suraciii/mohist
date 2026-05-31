# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/runner/src/actions/expectations.ts`, `packages/server/src/Mohist.Server/Workflow/Infrastructure/WorkflowYamlSerializer.cs`, `packages/server/src/Mohist.Server/Issue/WorkflowProfiles/mohist-default.workflow.yaml`, `packages/runner/tests/expectations.spec.ts`, `packages/server/tests/Mohist.Server.Tests/Specs/MohistDefaultWorkflowProfileSpecs.cs`
  Evidence: The change separates runtime diagnostics for task artifact failures and check verdict failures, but it does not complete the required schema and naming separation. Task artifact expectations still use the old generic contract `with.expect.files` and `with.expect.markers` in runner code (`packages/runner/src/actions/expectations.ts:13-28`), loader validation (`packages/server/src/Mohist.Server/Workflow/Infrastructure/WorkflowYamlSerializer.cs:90-107`), built-in profile definitions (`packages/server/src/Mohist.Server/Issue/WorkflowProfiles/mohist-default.workflow.yaml:16-18`, `26-28`, `36-38`, `46-48`, `56-58`, `140-142`), and focused tests (`packages/runner/tests/expectations.spec.ts:28-32`, `60-63`; `packages/server/tests/Mohist.Server.Tests/Specs/MohistDefaultWorkflowProfileSpecs.cs:237-242`, `266-271`). Issue #7 and its design explicitly require task expectation schema and naming to express artifact-focused requirements only, not just to reject PASS/FAIL-like values inside the old marker field.
  SuggestedAction: Rename or reshape the task artifact contract so task definitions expose artifact-focused fields for required files and optional neutral artifact content/markers, update built-in workflow profiles and focused tests to use that contract, and keep PASS/FAIL marker requirements solely on check definitions.
  Verification: Confirm no task definition or task-focused test still relies on the generic `expect.markers` contract, then rerun the runner expectation/verdict tests and the workflow profile spec coverage.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: `packages/runner/tests/openspec.spec.ts`
  Evidence: `npm test` in `packages/runner` still fails on `OpenSpecTaskWithoutExplicitPrompt_LoadsExecutableAcpTaskWithPrompt`, where the test expected `loaded` and received `failure`. This failure appears unrelated to the issue-7 task/check marker separation work.
  SuggestedAction: Investigate the `mohist/openspec-tasks` regression separately so the runner suite can return to green.
  Status: pre-existing

- [ID: item-3]
  Severity: warning
  Scope: `packages/web/src/pages/logs/model/useLogs.ts`
  Evidence: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter MohistDefaultWorkflowProfileSpecs` is blocked by the server project's web build, which fails with `TS2307: Cannot find module './api'` from `packages/web/src/pages/logs/model/useLogs.ts`. This prevented full server-side spec verification for the reviewed change and does not appear to be caused by the issue-7 workflow marker edits.
  SuggestedAction: Repair the missing web module import separately, then rerun the filtered server spec command.
  Status: pre-existing

<promise>FAIL</promise>
