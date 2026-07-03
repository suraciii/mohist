# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/runner/src/runtime/task-log.ts
  Evidence: The credential masker preserves the user-info username for `scheme://user:password@host` URLs (`packages/runner/src/runtime/task-log.ts:122-127`), and the tests explicitly lock in preserving `alice` / `ci` (`packages/runner/tests/task-log.spec.ts:15-27`, `packages/runner/tests/task-log.spec.ts:206-215`). A common token remote shape such as `https://ghp_abcdefghijklmnopqrstuvwxyz1234:x-oauth-basic@github.com/org/repo.git` therefore matches the first pattern and is persisted as `https://ghp_abcdefghijklmnopqrstuvwxyz1234:***@github.com/...`, leaking the token before buffering/upload/display. This violates the issue and spec requirement that git remote credentials are masked before the line reaches the buffer (`openspec/changes/issue-336/specs/ops-task-log-capture/spec.md:45-53`). [disallowed:security posture change]
  SuggestedAction: Mask the full user-info credential segment, or at minimum detect token-like usernames before preserving them. Add regression cases for token-as-username forms including `https://<token>:x-oauth-basic@...` and `https://<token>:@...`.
  Verification: Add the regression tests above, then run `npm run typecheck -w packages/runner` and `npm test -w packages/runner`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/runner/src/runtime/host.ts
  Evidence: `flushTaskLog` awaits `connection.uploadTaskLog(...)` with the main work signal (`packages/runner/src/runtime/host.ts:338-355`) and the normal path awaits that flush before calling `connection.report` (`packages/runner/src/runtime/host.ts:395-404`). If the task-log endpoint is slow or hangs, the verdict report is delayed indefinitely; if the shared signal aborts while upload is pending, the report path can be skipped. That conflicts with the design requirement that log upload is best-effort and must never block or fail the report (`openspec/changes/issue-336/design.md:167-170`). The current tests cover success and rejected upload (`packages/runner/tests/runner-host-task-log.spec.ts:142-211`) but not a pending upload.
  SuggestedAction: Bound the task-log upload with a short independent timeout/signal or otherwise ensure the verdict report is attempted even when upload never resolves. Add a host test where `uploadTaskLog` returns a never-resolving promise and assert `report` is still called promptly.
  Verification: Run the new pending-upload host test plus `npm test -w packages/runner`.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs, packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs
  Evidence: The task-log upload routes accept `workflowRunId` / `agentJobId` and `workId` directly from the URL (`packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs:34-52`) and immediately persist them (`packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs:86-90`). The store deletes existing entries for the tuple before inserting the new batch (`packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:54-63`, `packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:81-96`). The route specs even prove arbitrary ids are accepted (`packages/server/tests/Mohist.Server.Tests/Specs/Api/TaskLogRouteSpecs.cs:135-169`). By contrast, the adjacent artifact upload path resolves active work context and returns not-found when no active work exists (`packages/server/src/Mohist.Server/Workflow/Services/Artifacts/WorkflowArtifactUploadService.cs:115-117`, `packages/server/src/Mohist.Server/Api/WorkflowArtifactUploadRoutes.cs:137-140`). Any caller that reaches the internal endpoint can forge or erase review evidence for guessed owner/work ids. [disallowed:data safety/security posture change]
  SuggestedAction: Validate that the owner/work tuple corresponds to an active or otherwise authorized runner work item before writing. Preserve the no-status-adjudication invariant by validating against the runner work ledger or an equivalent runner-side authorization boundary, not by adding logs to `WorkResult`.
  Verification: Add negative API tests for unknown owner/work, cross-owner overwrite, and second-caller replacement; run `dotnet test Mohist.sln -p:SkipWebBuild=true`.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs, packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs
  Evidence: Batch count and total text caps are enforced only in the API route (`packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs:81-128`). `TaskLogService.AppendAsync` delegates directly to the store (`packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs:32-39`), while `TaskLogStore.ValidateEntries` checks per-entry shape and per-line length but not `MaxEntries` or `MaxTotalTextLength` (`packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:169-190`). Because both service and store are registered in DI (`packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:130-133`), an internal caller can bypass the same data-safety caps that protect the HTTP surface. [disallowed:data safety behavior change]
  SuggestedAction: Move authoritative batch caps into `TaskLogService` or `TaskLogStore`, leaving route validation as a precheck. Add service/store tests for `MaxEntries + 1` and total text over `MaxTotalTextLength`.
  Verification: Run the new store/service tests plus `dotnet test Mohist.sln -p:SkipWebBuild=true`.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: packages/runner/tests/runner-host-task-log.spec.ts
  Evidence: The suite verifies successful upload-before-report and rejected upload swallowed (`packages/runner/tests/runner-host-task-log.spec.ts:142-211`), but it does not test the critical best-effort case where `uploadTaskLog` never resolves. This missing test allowed item-2 to ship despite the comment claiming the report is never blocked (`packages/runner/src/runtime/host.ts:328-355`).
  SuggestedAction: Add a pending-upload test that fails unless `report` is attempted without waiting forever for task-log upload.
  Verification: Run `npm test -w packages/runner -- tests/runner-host-task-log.spec.ts`.
  Status: open

- [ID: item-6]
  Severity: test-gap
  Scope: packages/runner/tests/executor-task-log.spec.ts
  Evidence: The executor task-log tests cover action writes, `core/process`, and `core/script` (`packages/runner/tests/executor-task-log.spec.ts:64-218`), but they do not directly exercise workspace-prep, branch-check, or cleanup command paths. The implementation wires these sources in `WorkspaceManager`, branch stability, and clean-worktree enforcement (`packages/runner/src/runtime/workspace.ts:16-20`, `packages/runner/src/runtime/branch-stability.ts:11-15`, `packages/runner/src/runtime/worktree-enforcement.ts:22-26`), which are explicit acceptance criteria, but a source-label regression there would not fail the current task-log test surface.
  SuggestedAction: Add focused tests that produce captured lines from workspace preparation, branch stability, and cleanup and assert `workspace-prep`, `branch-check`, and `cleanup` source labels.
  Verification: Run the new tests plus `npm test -w packages/runner`.
  Status: open

- [ID: item-7]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Api/TaskLogRouteSpecs.cs
  Evidence: `UploadEndpoint_DoesNotInvokeAnyGrain` uploads to a non-existent workflow run and only asserts that no `WorkflowRuns` row was created (`packages/server/tests/Mohist.Server.Tests/Specs/Api/TaskLogRouteSpecs.cs:270-297`). That would not catch a future call to an existing workflow grain or report path. The independence requirement is central to the issue (`openspec/changes/issue-336/specs/task-log-persistence/spec.md:1-16`).
  SuggestedAction: Add a dependency-boundary test or spy/fake that fails on `WorkflowGrain` / `RunnerGrain.ReportWorkflowResultAsync` involvement, or structure the route/service dependencies so this cannot compile.
  Verification: Run `dotnet test Mohist.sln -p:SkipWebBuild=true`.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx
  Evidence: The web requirement says each rendered line displays timestamp, source, and text (`openspec/changes/issue-336/specs/task-log-viewer/spec.md:11-15`). The component renders a formatted timestamp (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:12-20`, `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:63-67`), but the test named for source/timestamp/text only asserts source labels and text, not the timestamp (`packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx:138-163`).
  SuggestedAction: Assert the rendered timestamp, for example `08:00:00.000`, in the line-rendering test.
  Verification: Run `npm run test:run -w packages/web -- TaskProgressPanel` and `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

- [ID: item-9]
  Severity: follow-up
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: `TaskLogPanel` auto-scrolls to the bottom when the number of lines changes (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:26-30`), but the test only checks that the panel is scrollable (`packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx:165-185`). Auto-scroll is useful for tail-retained failure context, but it is extra behavior beyond the explicit scrollable-panel acceptance criterion.
  SuggestedAction: Add a focused jsdom test that mocks `scrollHeight` and asserts `scrollTop` after lines render if auto-tail behavior is intended to be stable.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- None.

## Verification Summary

- `mo issue show 336 --project-id proj_f6c141d63b6243bfbb481737b2243b87` read and checked against the candidate.
- Reviewed proposal, design, tasks, specs, self-review, and all files changed relative to `origin/master...HEAD`.
- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner` passed: 63 files, 869 tests.
- `dotnet test Mohist.sln -p:SkipWebBuild=true` passed: 3647 passed, 13 skipped.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 259 files, 4076 passed, 1 skipped.
- An earlier all-in-one `npm test` attempt hit the 120s tool timeout while running combined verification; the constituent server, runner, and web commands passed when rerun separately.

<promise>FAIL</promise>
