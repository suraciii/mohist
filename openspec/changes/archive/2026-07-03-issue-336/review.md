# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/runner/src/runtime/workspace.ts, packages/runner/src/runtime/executor.ts
  Evidence: Workspace clone failures still copy the raw repository URL into the reported task message. `cloneFresh` invokes `git clone` with `gitUrl` and throws `git clone failed for ${gitUrl}: ...` on failure (`packages/runner/src/runtime/workspace.ts:195-203`). `workspaceSetupFailure` then copies that error message into `WorkItemResult.message` (`packages/runner/src/runtime/executor.ts:257-264`), and `ServerConnection.report` sends `message` unchanged (`packages/runner/src/server/connection.ts:59-81`). A credentialed remote such as `https://ghp_secret:x-oauth-basic@github.com/org/repo.git` can therefore leak through the task verdict even though task-log lines themselves are masked. This violates the issue's sensitive-output requirement because the Web still displays the unmasked failure message. [disallowed:security posture change]
  SuggestedAction: Reuse the task-log credential masker, or equivalent centralized sanitizer, for workspace setup error messages before they become report messages. Add a failed clone/workspace-setup test with a credentialed git URL and assert neither uploaded log lines nor reported `message` contain the token.
  Verification: Run `npm run typecheck -w packages/runner` and `npm test -w packages/runner` after adding the regression.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/runner/src/runtime/worktree-enforcement.ts
  Evidence: The cleanup stale-index-lock recovery path still runs an ops command outside the task-log sink. `recoverStaleIndexLock` captures the `git rev-parse --git-path index.lock` probe through the cleanup sink (`packages/runner/src/runtime/worktree-enforcement.ts:77-80`), but then calls `lockHolderProbe(workDir, lockPath, signal)` without passing the logger (`packages/runner/src/runtime/worktree-enforcement.ts:112`). The default probe runs `runCommand("lsof", [lockPath], ...)` without `onLine` (`packages/runner/src/runtime/worktree-enforcement.ts:131-134`). Any stdout/stderr from that cleanup command is not recorded with source `cleanup`, violating the single-sink and cleanup-source acceptance criteria. [disallowed:product behavior change]
  SuggestedAction: Thread the cleanup sink into `LockHolderProbe`/`defaultLockHolderProbe` and pass `onLine: line => log.write("cleanup", line)` for the `lsof` command. Add a stale-lock recovery test that exercises the default probe via a fake command runner and asserts the `lsof` output is captured as `cleanup`.
  Verification: Run the new stale-lock test plus `npm test -w packages/runner`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/runner/src/runtime/host.ts, packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs
  Evidence: Legitimate terminal batches can be lost by timing rather than by real upload failure. The runner races `uploadTaskLog` against a fixed 250 ms timeout and swallows timeout failures (`packages/runner/src/runtime/host.ts:348-371`, `packages/runner/src/runtime/host.ts:459`). The server then only accepts uploads while the runner-work row is still `Outstanding` (`packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs:48-50`). A near-capacity batch can be valid by the implemented caps (`MAX_TASK_LOG_LINES = 5000` in `packages/runner/src/runtime/task-log.ts:32`; server text cap in `packages/server/src/Mohist.Server/Infrastructure/TaskLogDtos.cs:29-35`) but still be dropped if upload/DB work takes longer than 250 ms, and it cannot be retried after report marks the work terminal. This undermines the Phase 1 expectation that logs are available after task completion. [disallowed:product behavior and data-safety trade-off]
  SuggestedAction: Decouple report progress from log upload without imposing a tiny fixed deadline on normal uploads, or allow authenticated same-runner terminal uploads for the just-finished work item. Add a host/service integration-style test where a valid large batch resolves after more than 250 ms and verify the intended durable behavior.
  Verification: Run the new delayed/large-batch tests plus `npm test -w packages/runner` and `dotnet test Mohist.sln -p:SkipWebBuild=true`.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: The Web viewer ignores cursor pagination. It always calls `useIssueWorkflowTaskLog(issueNumber, taskId, { limit: 5000 }, ...)` (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:22-24`) and renders only `data.lines` (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:32-69`). The API contract explicitly returns `nextCursor`, and the persistence spec requires clients to be able to fetch following pages when a page is not final (`openspec/changes/issue-336/specs/task-log-persistence/spec.md:43-57`). The server also allows a page max of 5000 while upload caps are higher (`packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:162-168`, `packages/server/src/Mohist.Server/Infrastructure/TaskLogDtos.cs:29-35`), so a valid non-null `nextCursor` response silently hides remaining log lines.
  SuggestedAction: Either fetch all pages for the retained log, or expose an explicit load-more control that follows `nextCursor`. Add a Web test where the first response has `nextCursor` set and assert additional lines can be reached.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
  Status: open

- [ID: item-5]
  Severity: minor
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx, packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: The expanded log UI is not keyboard/accessibility complete. The row button toggles expansion but does not expose `aria-expanded` or `aria-controls` (`packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx:67-95`). The scrollable log region uses `overflow-y-auto` but has no `tabIndex`, role, or accessible name (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:48-52`). Keyboard and assistive-tech users may not be able to discover or focus the long log region reliably.
  SuggestedAction: Add an id for the expanded content, wire `aria-expanded`/`aria-controls` on the button, and make the log region focusable with an accessible label such as `role="log"` or `aria-label="Execution log"` plus `tabIndex={0}`. Add Testing Library assertions for the expanded state and focusable log region.
  Verification: Run `npm run test:run -w packages/web -- TaskProgressPanel` and the full Web test suite.
  Status: open

- [ID: item-6]
  Severity: test-gap
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx, packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx
  Evidence: Timestamp rendering and its test are timezone-dependent. `formatTimestamp` parses an ISO timestamp and formats with local-time getters (`getHours`, `getMinutes`, etc.) (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:12-19`). The test feeds `2026-07-03T08:00:00.000Z` (`packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx:94-101`, `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx:138-158`) and expects `08:00:00.000`, which only holds when the test environment timezone is UTC.
  SuggestedAction: Format task-log timestamps explicitly in UTC, or compute the expected local display from the same formatter in the test. Add a targeted run under a non-UTC `TZ` to prove the test is deterministic.
  Verification: Run `TZ=Asia/Shanghai npm run test:run -w packages/web -- TaskProgressPanel` and the normal Web suite.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs
  Evidence: Upload size limits are enforced only after the entire JSON body is deserialized. `HandleUploadAsync` materializes `TaskLogUploadRequest` from `request.Body` first (`packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs:66-73`), and only then checks entry count and total text length (`packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs:82-89`, `packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs:119-151`). The limit constants cap entries/text after allocation (`packages/server/src/Mohist.Server/Infrastructure/TaskLogDtos.cs:29-35`), but they do not prevent an oversized request body from consuming memory before rejection. [disallowed:data-safety behavior change]
  SuggestedAction: Add request body size enforcement for the upload endpoint, for example via endpoint/request-size metadata or an early `Content-Length` guard plus bounded streaming parsing. Add API tests for bodies over the configured byte budget.
  Verification: Run the new API tests plus `dotnet test Mohist.sln -p:SkipWebBuild=true`.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: packages/runner/tests/git-sink.spec.ts, packages/runner/tests/process.spec.ts
  Evidence: New runner tests use real external processes in the default test suite, violating `design/testing.md`'s hard rule that tests must not touch real processes or real git/shell binaries (`design/testing.md:42-63`, `design/testing.md:144-148`). `git-sink.spec.ts` invokes real `git --version`, `git help status`, and a bogus git command (`packages/runner/tests/git-sink.spec.ts:6-22`, `packages/runner/tests/git-sink.spec.ts:24-37`, `packages/runner/tests/git-sink.spec.ts:39-68`). `process.spec.ts` spawns real Node child processes through `runCommand(process.execPath, ...)` (`packages/runner/tests/process.spec.ts:4-12`, `packages/runner/tests/process.spec.ts:80-115`, `packages/runner/tests/process.spec.ts:140-151`). These tests can fail on hosts without git/node in PATH or under process scheduling pressure, and they make the task-log coverage less deterministic.
  SuggestedAction: Replace real git/process usage with injected fakes or a mocked `child_process.spawn` harness that can deterministically emit stdout/stderr chunks, close events, and abort behavior.
  Verification: Run `npm test -w packages/runner` in an environment with no git binary available and with fake timers enabled for abort/timing cases.
  Status: open

- [ID: item-9]
  Severity: test-gap
  Scope: packages/runner test suite
  Evidence: The current snapshot did not satisfy the issue acceptance criterion that runner tests pass. `npm test -w packages/runner` failed with `tests/issue-112-regression.spec.ts > AgentTaskLeavesChanges_CleanupExhausts_TaskFailsBeforeDelivery` timing out after 5000 ms. A targeted rerun of `npm test -w packages/runner -- tests/issue-112-regression.spec.ts` passed, which points to a suite-level flake/load-sensitive timeout rather than a deterministic single-test failure, but the default runner suite is still not reliably green in this candidate.
  SuggestedAction: De-flake the regression by removing real-time/process sensitivity or increasing determinism with explicit fake signals. Also audit whether the new real-process tests in item-8 increase enough parallel load to trigger this timeout.
  Verification: Run `npm test -w packages/runner` repeatedly from a clean process; the command must pass without relying on isolated reruns.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- None identified separately from the current candidate. The runner-suite timeout may be pre-existing, but it occurred under the required verification command for this snapshot and therefore remains listed as a blocking verification item.

<promise>FAIL</promise>
