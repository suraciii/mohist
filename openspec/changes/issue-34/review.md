# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/pages/logs/model/useLogs.ts; packages/web/src/pages/logs/ui/LogsPage.tsx
  Evidence: When `/api/logs/tail` transitions from an available source to `unavailable=true`, `useLogs.applyResult` does not clear the previous entries because `BuildUnavailable` returns `reset=false` and the hook appends `result.lines` to the existing list (`useLogs.ts:40-48`). The page then renders the unavailable diagnostic branch (`LogsPage.tsx:188-211`), but the top-bar export button is still enabled from `filtered.length` and exports `filtered.map((e) => e.raw)` from stale entries (`LogsPage.tsx:86-97,119-122`). That violates the source-aware requirement that export operate against the real/current source; a user can be told logs are unavailable while exporting old logs from a source that is no longer available. [disallowed: product behavior change]
  SuggestedAction: Treat unavailable responses as a source reset in the hook by clearing `entries` and stored cursor/source state, or have the server mark unavailable as `reset=true`; also disable export while `unavailable` is true. Add a Web regression test that starts with populated available logs, then receives an unavailable response, and asserts old entries are not exported or retained as the active view.
  Verification: `npm run test:run -w packages/web -- src/pages/logs/model/useLogs.test.ts src/pages/logs/ui/LogsPage.test.tsx` passed 19/19, but current tests only cover an initial unavailable response and do not cover available-to-unavailable transitions or export state while unavailable.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/LogsRoutes.cs
  Evidence: The `maxBytes` cap is not actually enforced for an individual long log line. `ReadTailAsync` checks `bytesRead < maxBytes` before `ReadLineAsync`, then unconditionally adds the whole line and advances `bytesRead` after the read (`LogsRoutes.cs:159-173`). A request such as `?maxBytes=64` can therefore read, project, and return a multi-megabyte physical line; the cap only stops later iterations. This undercuts the per-request byte bound described by the design and leaves the public tail endpoint vulnerable to large-line memory/response amplification from a malformed or very large log record. [disallowed: public API behavior/security posture change]
  SuggestedAction: Enforce a documented maximum for `maxBytes` and make the read loop honor it before returning a line whose byte length exceeds the remaining budget, or explicitly define line-granular behavior with a hard per-line limit. Add route specs for `maxBytes` truncation, including a single line larger than the requested byte cap.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~LogsRouteSpecs` passed 18/18, but the suite covers line-count truncation and EOF cursor behavior, not byte-cap enforcement or large-line behavior.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Logging/FileLoggerProvider.cs
  Evidence: Log rotation, retention, and size bounding remain explicit non-goals, so `server.log` grows without a retention policy. The per-request `truncated` response only bounds reads; it does not bound disk growth.
  SuggestedAction: Track a follow-up for rotation/retention before the server log becomes long-lived operational data.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: packages/runner/tests/runner-host-task-log.spec.ts
  Evidence: The full `npm test` run completed server tests and Web tests, then failed in the unrelated runner suite with `RoutesConcurrentIncrementalUploadsToEachWorkItemCollector` timing out after 5000ms. Rerunning that exact test in isolation with `npm test -w packages/runner -- tests/runner-host-task-log.spec.ts -t RoutesConcurrentIncrementalUploadsToEachWorkItemCollector` passed 1/1 with the other 14 tests skipped, so this appears to be a full-suite runner flake or contention issue rather than part of the Logs-page/server-log change.
  SuggestedAction: Monitor or harden the runner task-log concurrency test separately; it does not block this issue's product deliverable directly, but it prevents a clean full-repo verification run.
  Status: out-of-scope

<promise>FAIL</promise>
