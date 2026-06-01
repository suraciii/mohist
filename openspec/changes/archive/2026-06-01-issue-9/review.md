# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: missing-obvious-guards | test expectation updates
  Evidence: The candidate now derives `busy` before treating a disconnected `workspace-query` transport as `offline`, so a fresh-heartbeat runner with active work remains connected busy capacity while still exposing `connectionState` as `disconnected` (`packages/server/src/Mohist.Server/Runner/Projection/RunnerStatusProjectionService.cs:100-114`). Backend and web regression coverage were updated to lock that behavior in (`packages/server/tests/Mohist.Server.Tests/Specs/RunnerStatusProjectionSpecs.cs:173-208`, `packages/server/tests/Mohist.Server.Tests/Specs/RunnerStatusApiSpecs.cs:185-224`, `packages/web/tests/runner-status.test.ts:35-80`).
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests --filter "RunnerStatus|RuntimeEntry"`
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: completeness
  Evidence: The detailed runner list now renders `capabilities`, and the web tests assert that user-facing capability diagnostics are visible (`packages/web/src/widgets/runner-status/ui/RunnerList.tsx:93-97`, `packages/web/tests/runner-status.test.tsx:343-366`).
  Verification: Covered by the candidate snapshot; the repo-level `npm test -- runner-status` wrapper is malformed because the root `test` script maps directly to `dotnet test Mohist.sln`, so targeted web verification should be run via the package workspace script instead.
  Status: resolved

## Blocking Items

- None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Runner/Projection/RunnerStatusProjectionService.cs`
  Evidence: `ProjectRunnerAsync` still accepts `projectId` but never uses it (`packages/server/src/Mohist.Server/Runner/Projection/RunnerStatusProjectionService.cs:36`). It does not break current behavior because registry filtering already scopes the runner set, but it adds avoidable noise to the projection API.
  SuggestedAction: Remove the unused parameter or use it when project-aware enrichment is actually needed.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: verification workflow
  Evidence: The verification command suggested by the issue prompt, `npm test -- runner-status`, does not work in this repo because the root `test` script is `dotnet test Mohist.sln`, so the extra token is treated as a second project path by MSBuild and fails with `MSB1008`.
  SuggestedAction: Use the workspace-level web test command for future verification, for example `npm test -w packages/web -- --run tests/runner-status.test.ts tests/runner-status.test.tsx`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- None.

<promise>PASS</promise>
