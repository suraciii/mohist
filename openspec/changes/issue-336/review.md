# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/runner/src/actions/registry.ts
  Evidence: `core/process` and `core/script` are explicit shell/process ops actions, but their command invocations still call `runCommand(...)` without line-capture options at `packages/runner/src/actions/registry.ts:74` and `packages/runner/src/actions/registry.ts:89`. Their stdout/stderr therefore never reaches `ActionContext.log.write`, despite the issue requiring shell command output to be captured line-by-line and the spec requiring every ops command output to flow through the single sink. [disallowed:product-behavior-change]
  SuggestedAction: Add sink forwarding for these built-in action bodies, using action source tags such as `action:process` and `action:script`, while preserving the aggregate `CommandResult` behavior. Add executor tests that run `core/process` and `core/script` with stdout/stderr and assert the collector receives the lines.
  Verification: Reproduce with a `core/script` task that writes stdout/stderr and inspect `execution.collector.flush().entries`; currently it will not contain those lines.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/runner/src/actions/create-github-pr.ts; packages/runner/src/actions/mark-github-pr-ready.ts; packages/runner/src/actions/merge-github-pr.ts; packages/runner/src/actions/github-pr-status.ts; packages/runner/src/actions/github-pr-runtime.ts
  Evidence: The GitHub PR actions wire the sink for some `git(...)` calls but not for `gh` CLI calls. `runGhPrecheck` invokes `gh --version` and `gh auth status` without `onLine` at `packages/runner/src/actions/github-pr-runtime.ts:31-44`; create PR calls `gh pr list/edit/create` without capture at `packages/runner/src/actions/create-github-pr.ts:156`, `:172`, and `:188`; ready/merge/status siblings have the same pattern at `packages/runner/src/actions/mark-github-pr-ready.ts:59` and `:96`, `packages/runner/src/actions/merge-github-pr.ts:113`, and `packages/runner/src/actions/github-pr-status.ts:135`. Failures from `gh` are ops command output, but they bypass the task log sink and will not be visible in Web logs. [disallowed:product-behavior-change]
  SuggestedAction: Thread a task-log line callback/source into the `gh` runner path and tests, mirroring the `git` sink behavior across create/ready/merge/status actions.
  Verification: Stub `getGitHubPrGh()` to emit stdout/stderr during GitHub PR actions, run with `context.log`, and assert the collector receives action-tagged `gh` output.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx; packages/web/src/entities/issue/api/queries.ts
  Evidence: The viewer performs one query with `{}` at `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:20` and renders only `data.lines` at `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:29-67`. It never follows `nextCursor`. The server default page size is 500 (`packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:129` and `:159`), while the runner retains up to 5000 lines (`packages/runner/src/runtime/task-log.ts:32`). Long retained logs therefore show only the first page, so the user may never see the failure tail. This violates the requirement that retained tail lines be fully rendered and scrollable. [disallowed:product-behavior-change]
  SuggestedAction: Use an infinite query/load-more/load-all path until `nextCursor` is null, or request and test a limit that covers the runner retention cap. Add a test where the failure line is on page 2.
  Verification: Seed or mock a task-log response with `nextCursor != null` on the first page and the conflict line only on the second page; the current panel cannot display it.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx
  Evidence: The task row only toggles expansion when `isFailed` is true at `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx:70`, and the expanded region including `TaskLogPanel` is rendered only under `expanded && isFailed` at `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx:94-171`. The new test also locks in this behavior by asserting the log panel is not rendered for completed tasks at `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx:237`. The issue requires logs to be viewable after ops task execution, not only after failures. Successful ops tasks with captured logs have no way to view them here. [disallowed:product-behavior-change]
  SuggestedAction: Allow expansion/log viewing for completed ops tasks with logs while preserving existing failure guidance. Add tests for a completed task with captured log lines.
  Verification: Mock a completed task with log lines, render `TaskProgressPanel`, and confirm there is currently no path to open `TaskLogPanel`.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: packages/web/src/entities/issue/api/queries.ts; packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: The task-log query key is `[issueNumber, safeTaskId, projectId, 'workflow-task-log', params]` at `packages/web/src/entities/issue/api/queries.ts:31-34`, and `TaskLogPanel` passes only `issueNumber` and `taskId` at `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:19-20`. The server resolves the issue's current workflow run at request time (`packages/server/src/Mohist.Server/Api/IssueRoutes.TaskLog.cs:36-41`). If an issue is rerun and the same task id is reused, the Web cache key does not change with `workflowRunId`, so the panel can keep showing the previous run's log. [disallowed:product-behavior-change]
  SuggestedAction: Include the active `workflowRunId` in the query key, or invalidate task-log queries when the timeline workflow run changes. Add a rerun/cache test where `workflowRunId` changes while task id stays the same.
  Verification: Expand a task log, rerun the issue so the same task id maps to a new workflow run, and observe whether the old cached lines remain visible.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs; packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs; packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs
  Evidence: Upload accepts `seq` values without uniqueness or monotonic validation (`packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs:86-94`), `TaskLogStore.AppendAsync` inserts them as-is (`packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:82-92`), and the `(OwnerKind, OwnerId, WorkId, Seq)` index is non-unique (`packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs:620-621`). Pagination filters with `Seq > cursor` at `packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:137-145`; duplicate seq values can be skipped permanently when `limit` splits them across pages. This undermines the seq-based cursor contract. [disallowed:data-safety]
  SuggestedAction: Enforce a unique `(OwnerKind, OwnerId, WorkId, Seq)` constraint and/or reject uploads whose seq values are not strictly increasing and positive. Add store/API tests for duplicate and non-monotonic seq values.
  Verification: Upload two entries with `seq = 1`, query with `limit=1`, then query `cursor=1`; one row is currently skipped.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs
  Evidence: `limit` is caller-controlled except for null or `<= 0` at `packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:129`, then the query computes `Take(pageSize + 1)` at `packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:143-146`. A very large positive limit can force an oversized read, and `int.MaxValue` overflows `pageSize + 1`. [disallowed:data-safety]
  SuggestedAction: Clamp `limit` to a fixed maximum and guard overflow. Add tests for huge limits and `int.MaxValue`.
  Verification: Call `GET .../logs?limit=2147483647`; the current implementation does not clamp before `pageSize + 1`.
  Status: open

- [ID: item-8]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs; packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogEntryRow.cs
  Evidence: The upload route only caps entry count (`packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs:81-84`). Missing/null `source` and `text` are coerced to empty strings at `packages/server/src/Mohist.Server/Api/TaskLogRoutes.cs:86-91`, missing timestamps deserialize to defaults, and `Text` is an unbounded SQLite `TEXT` column (`packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogEntryRow.cs:43-49`). A malformed or oversized internal request can persist invalid metadata or large log payloads despite the data-safety requirements. [disallowed:data-safety]
  SuggestedAction: Validate seq, timestamp, source, text length, and total payload size at the route or service boundary; reject invalid uploads with 400.
  Verification: POST one entry with missing timestamp, empty source, and multi-MB text; it is currently accepted unless entry count exceeds 20000.
  Status: open

- [ID: item-9]
  Severity: warning
  Scope: packages/runner/src/runtime/task-log.ts; packages/runner/src/runtime/executor.ts
  Evidence: `CredentialMasker.registerSecret` exists (`packages/runner/src/runtime/task-log.ts:75-78`), but production creates `TaskLogger` with a default masker and never registers runtime-known secrets (`packages/runner/src/runtime/executor.ts:71-74`). The proposal/design call out runner-configured secrets in the masking path, so secrets that do not match the built-in regex catalog can still be buffered and uploaded. [disallowed:security-posture]
  SuggestedAction: Decide the runtime secret inventory and register those values when constructing the per-work logger, or narrow the documented contract. Add a test where a configured non-pattern secret appears in command output and is redacted before buffering.
  Verification: Log a configured secret such as a runner token that does not match the built-in patterns; the current collector stores it raw.
  Status: open

- [ID: item-10]
  Severity: minor
  Scope: packages/runner/src/system/process.ts
  Evidence: `runCommand` decodes each `Buffer` chunk independently with `chunk.toString("utf8")` at `packages/runner/src/system/process.ts:77-84`. If a multi-byte UTF-8 character is split across chunks, the line callback can receive replacement characters even though the aggregate `Buffer.concat(...).toString("utf8")` remains correct. This is a new line-capture edge case for filenames or command output containing non-ASCII text. [disallowed:product-behavior-change]
  SuggestedAction: Use a `StringDecoder` per stream for the `onLine` path and add a test that splits a multi-byte character across writes.
  Verification: Spawn a child that writes the bytes of one non-ASCII character in two chunks before `\n`; the current `onLine` path can corrupt the captured line.
  Status: open

- [ID: item-11]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Api/TaskLogRouteSpecs.cs
  Evidence: New server integration tests use wall-clock time in `SeedWorkflowRunAsync` at `packages/server/tests/Mohist.Server.Tests/Specs/Api/TaskLogRouteSpecs.cs:82` and in `UploadEndpoint_AgentJobRoute_StoresUnderAgentJobOwnerKind` at `packages/server/tests/Mohist.Server.Tests/Specs/Api/TaskLogRouteSpecs.cs:182`. The repo testing rules require injectable/fake time, and this file already has `_fixture.TimeProvider` available. [disallowed:test-only-policy]
  SuggestedAction: Replace those `DateTimeOffset.UtcNow` calls with `_fixture.TimeProvider.GetUtcNow()` and keep assertions deterministic.
  Verification: Review the two cited lines or run a grep for `DateTimeOffset.UtcNow` in the new task-log specs.
  Status: open

- [ID: item-12]
  Severity: test-gap
  Scope: packages/runner test suite
  Evidence: `npm test -w packages/runner` failed in the full suite: `tests/executor-artifacts.spec.ts > WorkExecutor artifact capture > declaredDirectoryExceedsLimitsIsNonFatalWarning` timed out after 5000 ms. The same test passed in isolation with `npm test -w packages/runner -- tests/executor-artifacts.spec.ts -t declaredDirectoryExceedsLimitsIsNonFatalWarning`, which points to suite timing or flakiness, but the issue acceptance requires existing runner tests not to regress. [disallowed:test-suite-reliability]
  SuggestedAction: Investigate and stabilize the full runner suite before integration, or prove and document that the failure is pre-existing with a clean base comparison.
  Verification: `npm test -w packages/runner` currently returns exit code 1 in this candidate snapshot; isolated rerun of the failing test passes.
  Status: open

## Follow-up Items

- [ID: item-13]
  Severity: follow-up
  Scope: packages/web/src/entities/issue/api/queries.ts
  Evidence: The hook maps every `ApiError` 404 to an empty log page at `packages/web/src/entities/issue/api/queries.ts:20-23`. This supports older servers without the endpoint, but the new server's no-log path is already a 200 empty page. A real missing project/issue/task route could be hidden as "no execution log captured".
  SuggestedAction: Consider narrowing the compatibility fallback or surfacing unexpected 404s once the endpoint is always present.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-14]
  Severity: info
  Scope: verification
  Evidence: `npm run typecheck -w packages/runner`, `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, `dotnet test Mohist.sln -p:SkipWebBuild=true`, and `git diff --check master...HEAD` passed. Repo-level `npm test` was also attempted but hit the 120s tool timeout during its .NET phase, so it was not a reliable final signal by itself.
  SuggestedAction: Use the package-specific results above plus a rerun of full `npm test` after fixing the blocking issues.
  Status: out-of-scope

<promise>FAIL</promise>
