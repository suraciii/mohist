# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: missing-obvious-guards
  Evidence: The candidate includes a local repair for `FileLoggerProvider.IsEnabled`: `LogLevel.None` previously compared greater than `Information` and would be treated as enabled. `packages/server/src/Mohist.Server/Logging/FileLoggerProvider.cs:66` now explicitly rejects `LogLevel.None`, and `packages/server/tests/Mohist.Server.Tests/Specs/Logging/FileLoggerProviderSpecs.cs:171` covers it.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~LogsRouteSpecs|FullyQualifiedName~FileLoggerProviderSpecs|FullyQualifiedName~LogPathResolverSpecs"` passed: 32 passed, 0 skipped, 0 failed. `dotnet test Mohist.sln -p:SkipWebBuild=true` passed: 3844 passed, 13 skipped, 0 failed.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Api/LogsRoutes.cs`
  Evidence: The spec requires that a first uncursored request return the recent tail (`openspec/changes/issue-34/specs/logs-tail-api/spec.md:29`), but the implementation always starts a first read at byte `0` (`packages/server/src/Mohist.Server/Api/LogsRoutes.cs:70-76`). With a long daemon log and a capped first request, `/api/logs/tail` returns the oldest startup chunk instead of the newest records. The current route spec locks in that wrong behavior by expecting `line 1` and `line 2` for `?limit=2` over a five-line file (`packages/server/tests/Mohist.Server.Tests/Specs/Api/LogsRouteSpecs.cs:174-199`). The UI then renders the truncation banner text "showing latest chunk" (`packages/web/src/pages/logs/ui/LogsPage.tsx:180-183`) even though the server returned the oldest chunk. [disallowed:reason] Repair requires changing public cursor/tail semantics and updating server/Web expectations.
  SuggestedAction: For `cursor == null`, compute an EOF-relative start window bounded by `limit`/`maxBytes`, return the newest records, and set the returned cursor to the continuation point for subsequent polling. Update route specs so `?limit=2` over five lines returns the last two records, and append polling starts after the returned EOF cursor.
  Verification: Add the recent-tail regression spec, then run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~LogsRouteSpecs` and `dotnet test Mohist.sln -p:SkipWebBuild=true`.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Api/LogsRoutes.cs`
  Evidence: `maxBytes` truncation can skip a normal line forever. After at least one entry has been returned, `ReadTailAsync` calls `ReadLineWithinBudgetAsync` with only the remaining byte budget (`packages/server/src/Mohist.Server/Api/LogsRoutes.cs:170-188`). If the next line is larger than the remaining budget but smaller than the configured request cap, `ReadLineWithinBudgetAsync` drains the rest of that line (`packages/server/src/Mohist.Server/Api/LogsRoutes.cs:217-223`), `ReadTailAsync` advances `nextCursor` past it, and the response does not include it. Passing the cursor back starts after the skipped line, violating the incremental cursor requirement (`openspec/changes/issue-34/specs/logs-tail-api/spec.md:31-35`). Current coverage only checks a single oversized first line should be skipped (`packages/server/tests/Mohist.Server.Tests/Specs/Api/LogsRouteSpecs.cs:333-356`), not the chunk-boundary case where the line should be returned on the next request. [disallowed:reason] Repair changes tail cursor and byte-budget behavior.
  SuggestedAction: Track each candidate line's starting offset. If a budget overrun happens after at least one entry was already returned, leave `nextCursor` at that line's start and return `truncated=true`; only drain and skip when the first line in an otherwise empty chunk itself exceeds `maxBytes`. Add a route spec where line 1 fits and line 2 crosses only the remaining budget, then assert line 2 appears on the next cursor request.
  Verification: Add the boundary regression spec, then run the focused `LogsRouteSpecs` filter and the full server suite.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/web/src/shared/lib/log-levels.ts`, `packages/web/src/pages/logs/ui/LogsPage.tsx`, `packages/server/src/Mohist.Server/Logging/FileLogger.cs`
  Evidence: The server emits critical logs as `FATAL` (`packages/server/src/Mohist.Server/Logging/FileLogger.cs:58-66`), but the Web level model only contains `DEBUG`, `INFO`, `WARN`, and `ERROR` (`packages/web/src/shared/lib/log-levels.ts:1-16`). The page initializes the enabled set from that list and hides any non-null level that is not in the set (`packages/web/src/pages/logs/ui/LogsPage.tsx:29,51-55`). As a result, a `FATAL` server log is hidden by default and there is no chip to enable it, even with all visible level filters active. [disallowed:reason] Repair changes the public level vocabulary shared by Logs and Settings.
  SuggestedAction: Align the Web level model with server-emitted levels, at minimum by adding `FATAL` with chip/color/test coverage or by normalizing `FATAL` to `ERROR` before filtering. Add a LogsPage regression test proving a `FATAL` entry is visible with default filters.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` after updating the level tests and LogsPage tests.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: `packages/web/src/pages/logs/model/useLogs.ts`, `packages/web/src/pages/logs/ui/LogsPage.tsx`
  Evidence: Once the hook has received `unavailable=true`, a later fetch failure only sets `error` and leaves the stale `unavailable` state intact (`packages/web/src/pages/logs/model/useLogs.ts:72-74`). The page renders the unavailable branch before checking `error` (`packages/web/src/pages/logs/ui/LogsPage.tsx:192-220`), so a current API/network failure is masked by the previous source-unavailable diagnostic. This is a recovery-path regression for an operational diagnostics page. [disallowed:reason] Repair changes visible error precedence/state semantics.
  SuggestedAction: Either clear `unavailable` on fetch errors or render the current error before the unavailable diagnostic. Add a Web test that first returns an unavailable response, then rejects the next poll/refresh, and asserts the error is visible.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Logging/FileLogger.cs`, `packages/server/src/Mohist.Server/Logging/LogRecord.cs`
  Evidence: `LogRecord` has a `Fields` property (`packages/server/src/Mohist.Server/Logging/LogRecord.cs:14-20`), and T-001 acceptance requires written records to contain exception/fields when present (`openspec/changes/issue-34/tasks.json:14`). `FileLogger.Log` only records the formatted message and exception (`packages/server/src/Mohist.Server/Logging/FileLogger.cs:47-55`); it never copies structured logging state such as `logger.LogInformation("hello {name}", "world")` into `Fields`. The existing format test uses that structured message but only asserts the formatted string (`packages/server/tests/Mohist.Server.Tests/Specs/Logging/FileLoggerProviderSpecs.cs:189-206`). [disallowed:reason] Repair changes the on-disk log record contract and serialization behavior.
  SuggestedAction: When `state` is `IEnumerable<KeyValuePair<string, object?>>`, copy structured entries except `{OriginalFormat}` into `LogRecord.Fields`, preserving exception behavior. Add a unit test asserting `fields.name == "world"` for a structured logging call.
  Verification: Run the `FileLoggerProviderSpecs` filter and `dotnet test Mohist.sln -p:SkipWebBuild=true`.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Program.cs`
  Evidence: The new file logger is registered on both primary and alternate hosts (`packages/server/src/Mohist.Server/Program.cs:16,112`), but the OTLP bind-failure recovery messages still bypass `ILogger` and write only to `Console.Error` (`packages/server/src/Mohist.Server/Program.cs:92-99,145-158`). That means a startup recovery event that directly affects daemon behavior may not appear in `/api/logs/tail`, despite this issue's goal that the Logs page diagnose server daemon behavior and the task note that the fallback host logs identically. [disallowed:reason] Repair changes startup/recovery logging behavior.
  SuggestedAction: Emit the bind-failure and generic startup-failure diagnostics through an `ILogger` that is backed by the file provider, optionally mirroring to `Console.Error`. Add focused coverage for the recovery logging helper or startup fallback path.
  Verification: Run the relevant OTLP recovery tests plus `dotnet test Mohist.sln -p:SkipWebBuild=true`.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: `packages/web/src/pages/logs/ui/LogsPage.tsx`, `packages/web/src/pages/logs/ui/LogsPage.test.tsx`
  Evidence: Export is acceptance-critical (`openspec/changes/issue-34/specs/logs-page/spec.md:56-60`; `openspec/changes/issue-34/tasks.json:66`), and the implementation exports `filtered.map((e) => e.raw).join('\n')` (`packages/web/src/pages/logs/ui/LogsPage.tsx:86-99`). The page tests only assert export is disabled while unavailable (`packages/web/src/pages/logs/ui/LogsPage.test.tsx:107-122`); they never assert the exported `Blob` contains the currently filtered entries or uses each entry's `raw`. [disallowed:reason] Not repaired because this is test coverage, not a local typo/guard.
  SuggestedAction: Mock `URL.createObjectURL`, capture the `Blob`, trigger Export after applying a filter/search, and assert the blob text equals the visible filtered entries' `raw` joined by newlines.
  Verification: Run `npm run test:run -w packages/web -- src/pages/logs/ui/LogsPage.test.tsx` and the full Web suite.
  Status: open

- [ID: item-9]
  Severity: test-gap
  Scope: `packages/web/src/pages/logs/ui/LogsPage.tsx`, `packages/web/src/pages/logs/ui/LogsPage.test.tsx`
  Evidence: The logs-page spec requires rows to display preserved level/time/service/message fields (`openspec/changes/issue-34/specs/logs-page/spec.md:11-15`). `LogRow` renders those fields (`packages/web/src/pages/logs/ui/LogsPage.tsx:8-23`), but the page tests mostly assert messages, filtering, source, and diagnostics; they do not assert that a structured entry's time, level chip, and service are rendered from the agreed element type (`packages/web/src/pages/logs/ui/LogsPage.test.tsx:175-224`). [disallowed:reason] Not repaired because this is a missing regression test.
  SuggestedAction: Add a LogsPage test with a `WARN` entry carrying `time`, `service`, `message`, and `raw`, and assert the row displays the formatted time, `WARN`, service, and message.
  Verification: Run the focused LogsPage test and full Web suite.
  Status: open

- [ID: item-10]
  Severity: test-gap
  Scope: `packages/web/src/pages/logs/ui/LogsPage.tsx`, `packages/web/src/pages/logs/ui/LogsPage.test.tsx`
  Evidence: Auto-follow pause-on-scroll-up is part of the accepted behavior (`openspec/changes/issue-34/specs/logs-page/spec.md:61-65`; `openspec/changes/issue-34/tasks.json:66`). The page implements scroll suppression with `userPausedAutoFollow` and `scrollIntoView` (`packages/web/src/pages/logs/ui/LogsPage.tsx:63-84`), but `LogsPage.test.tsx` has no coverage for scroll pause/resume or suppressed auto-scroll. Hook tests cover polling/visibility, not the page's scroll behavior. [disallowed:reason] Not repaired because this is a missing regression test.
  SuggestedAction: Mock `scrollIntoView`, simulate scrolling away from the bottom, rerender with a new entry, and assert auto-scroll is suppressed until the container is near the bottom again.
  Verification: Run the focused LogsPage test and full Web suite.
  Status: open

## Follow-up Items

- [ID: item-11]
  Severity: follow-up
  Scope: server log file lifecycle
  Evidence: Log rotation, retention, and size-bounding are explicit non-goals (`openspec/changes/issue-34/design.md:37,156`), so the new `server.log` grows without a disk-retention policy. The per-request `truncated` flag bounds UI/API reads, not storage growth.
  SuggestedAction: Track a follow-up for rotation/retention before server daemon logs become long-lived operational data.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-12]
  Severity: info
  Scope: repository test suite
  Evidence: `dotnet test Mohist.sln -p:SkipWebBuild=true` passed but reported 13 skipped tests in unrelated telemetry, architecture, session, issue-profile, and issue-feedback areas. `npm run test:run -w packages/web` passed but reported 1 skipped Web test. These skips are not introduced by this change, but they mean the broad suites are not fully exhaustive.
  SuggestedAction: Audit skipped tests separately if they are no longer intentional.
  Status: pre-existing

<promise>FAIL</promise>
