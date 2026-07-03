## Why

When an ops task fails, the Web only shows the terminal verdict — `status` + a
short `message` + the structured `output` JSON. The actual command output that
explains *why* it failed (which commit conflicted, which file, what git printed)
is discarded inside the runner or collapsed into an aggregated `combinedOutput`
string that never leaves the process. Users cannot locate a failure without
SSH-ing in and re-running the command by hand. We need a fourth class of task
evidence — the **execution process** — captured line-by-line and viewable in the
Web, distinct from artifacts (produced files), AgentSession transcript (agent
dialogue), and the output JSON (structured conclusion). This issue delivers the
end-to-end Phase 1 loop: terminal-state batch (no real-time streaming yet).

## What Changes

- Add a single sink for ops command output in the runner (`ActionContext.log`),
  replacing the current pattern of each call site assembling its own
  `combinedOutput`. All ops output (git/shell) flows through one point.
- Upgrade `runCommand` (`system/process.ts`) with an optional `onLine` callback
  that emits stdout/stderr **merged** line-by-line (no stream dimension),
  preserving lines even when the child omits a trailing newline, with a
  post-exit drain so no pending output is lost. The existing aggregate return
  contract is unchanged so current call sites do not regress.
- Wire executor phases (`prepareWorkspace`, `checkBranchStability`, the action
  body, `enforceCleanWorktree`) to emit lines tagged with a `source` (e.g.
  `workspace-prep`, `branch-check`, `action:rebase`, `cleanup`).
- Apply minimal secret masking **at the sink entry**, before buffering — so
  buffered, uploaded, and displayed data is already masked (covers known
  credential patterns like git remote URLs with embedded credentials).
- Add a per-work `TaskLogCollector` that buffers entries with monotonic `seq`,
  timestamp, and source; on task completion it flushes once (Phase 1 terminal
  batch). Over-capacity logs are truncated by **dropping the head and keeping
  the tail** (error context), with a `truncated` marker; discarded `seq` values
  are not reused.
- Add an **independent** upload channel mirroring artifact uploads:
  `POST /api/{workflow-runs|agent-jobs}/{ownerId}/work/{workId}/task-log` writes
  to a dedicated store. `report` / `WorkResult` / the `WorkflowRun` aggregate are
  **not** changed — TaskLog never participates in status adjudication.
- Add server storage `TaskLogEntries` (owner-kind + owner-id + work-id + seq),
  a `TaskLogStore` under `Infrastructure/Data/Runner/` (no grain involvement),
  and a query API via the issue path
  `GET /api/projects/{projectId}/issues/{number}/workflow/tasks/{taskId}/logs`
  with cursor pagination returning `{ lines, nextCursor, truncated }`.
- Add a Web panel in the task expand area that renders the log line-by-line,
  scrollable, with timestamp and source label, so a deliberately broken ops task
  (e.g. a rebase conflict) can be scrolled to the failing command's real output.

## Capabilities

- `ops-task-log-capture`: Runner-side capture pipeline — the single sink
  (`ActionContext.log.write`), `runCommand.onLine` merged line-by-line emission
  with no-loss guarantees (trailing-newline handling + post-exit drain), secret
  masking at entry, monotonic `seq` + timestamp + `source` tagging, the per-work
  `TaskLogCollector` buffer, terminal-batch flush, and head-drop/tail-keep
  capacity truncation. Also the contract that existing ops call sites
  (`git()`, `runCommand`) keep returning their full aggregate output unchanged.
- `task-log-persistence`: Server-side independent storage and query — TaskLog
  stored apart from `WorkflowRun`/`WorkResult`/`report` (independent channel,
  no grain, no status-adjudication coupling), owner-kind routing
  (`workflow-runs` vs `agent-jobs`), the POST upload endpoint, the
  `TaskLogEntries` store/table, and the issue-path GET query with cursor
  pagination and `truncated` reporting.
- `task-log-viewer`: Web display — the task expand area renders the captured
  execution log line-by-line, scrollable, with timestamp and source label, so a
  failed ops task's real command output is readable without leaving the UI.

## Impact

- **Runner (TypeScript)**: `system/process.ts` (`runCommand` gains `onLine`);
  `actions/git.ts` (forward lines to the sink while preserving its return
  contract); new `TaskLogger` + `TaskLogCollector` + minimal masker; executor
  wiring in `runtime/executor.ts` and the workspace/branch-stability/worktree
  helpers; `server/connection.ts` gains a task-log upload call.
- **Server (C#)**: new `TaskLogEntryRow` + `TaskLogStore` under
  `Infrastructure/Data/Runner/`; new `TaskLogRoutes` (POST upload + GET query,
  dual owner-kind routes, mirroring `WorkflowArtifactUploadRoutes` and
  `IssueRoutes.Artifacts`); a EF Core migration for `TaskLogEntries`. The
  `Workflow/` domain, `WorkflowGrain`, `RunnerGrain.ReportWorkflowResultAsync`,
  and `WorkResult` are **untouched**.
- **Web (React)**: a log panel in the task expand area of
  `TaskProgressPanel.tsx` plus a query hook against the new issue-path endpoint;
  existing task status/message/output rendering unchanged.
- **APIs/Data**: one new internal upload endpoint and one new issue-path query
  endpoint; no changes to `report` or any existing workflow API contract.
- **Tests**: runner unit (capture pipeline, masking, truncation, no-loss drain,
  call-site non-regression) + spec; server unit (store append/query, owner-kind
  routing) + spec (endpoint, independence from report); web render + a11y as
  appropriate. Existing runner/server/web suites must not regress.
