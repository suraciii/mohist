# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Api/LogsRoutes.cs; packages/web/src/pages/logs/model/useLogs.ts
  Evidence: Cursor-based auto-follow is still broken after any untruncated read. The route computes the byte position from `ReadTailAsync` but then returns `cursor`/`nextCursor` only when `truncated` is true (`LogsRoutes.cs:48-55`), so a normal first read that reaches EOF sends `nextCursor: null`. The hook stores that null (`useLogs.ts:49`) and treats null as "call without cursor" on the next poll (`useLogs.ts:64-66`), which makes the server start at byte 0 again, return `reset: true`, and replay the whole file instead of waiting at EOF and returning only appended lines. This violates the issue acceptance criteria for incremental cursor tailing and source-backed auto-follow. [disallowed: product behavior/API contract change]
  SuggestedAction: Return the current EOF byte offset as `cursor`/`nextCursor` even when `truncated` is false, using `truncated=false` to indicate no further chunk is immediately available. Add a regression test where an initial untruncated read returns a non-null cursor, the next poll with that cursor returns zero lines with `reset=false`, and appending a new line then returns only that new line.
  Verification: Existing targeted tests pass but encode the bug: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~LogsRouteSpecs` passed 10/10; `npm run test:run -w packages/web -- src/pages/logs/model/useLogs.test.ts src/pages/logs/ui/LogsPage.test.tsx` passed 18/18.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Api/LogsRouteSpecs.cs; packages/web/src/pages/logs/model/useLogs.test.ts
  Evidence: The tests lock in the broken EOF cursor behavior instead of guarding the required auto-follow path. `LogsRouteSpecs.cs:150-152` and `LogsRouteSpecs.cs:193-200` assert that EOF responses have null cursors, while `useLogs.test.ts:153-154` treats null-after-read as the expected reason a refresh calls without a cursor and replaces entries. The Web auto-follow tests only cover the non-null `nextCursor` case (`useLogs.test.ts:269-310`), so the most common path, a first read that reaches EOF, is untested.
  SuggestedAction: Change the tests to assert a stable EOF cursor and add a cross-boundary auto-follow regression that covers first read to EOF, empty poll at EOF, append after EOF, and no duplicate replay.
  Verification: Reviewed the current test assertions and ran the targeted server/Web logs suites listed in item-1.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/LogsRoutes.cs
  Evidence: Available `/api/logs/tail` responses expose the absolute log file path even though the design intentionally uses `source` as a file name to avoid leaking absolute paths. `LogsRoutes.cs:60-69` sets `ExpectedLocation: expectedFile` for the normal available response, while the contract/design only need `expectedLocation` for the unavailable diagnostic. This exposes local filesystem layout on every successful tail response, not just when the user needs a diagnostic. [disallowed: public API/security posture change]
  SuggestedAction: Return `expectedLocation: null` whenever `unavailable` is false, keep the absolute expected path only in unavailable responses, and add an API contract test for both states.
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true` passed 3832/3845 with 13 skipped, but no test asserts `expectedLocation` is null on available responses.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/LogsRoutes.cs
  Evidence: Query parameters are not validated before being used as file offsets and collection sizes. A negative `cursor` reaches `stream.Seek(startPosition, SeekOrigin.Begin)` (`LogsRoutes.cs:46,140-141`), and a negative `limit` reaches `new List<LogEntry>(capacity: Math.Min(limit, 256))` (`LogsRoutes.cs:133-136`), both of which can turn a public read endpoint into a 500. Zero or negative `limit`/`maxBytes` also produce misleading truncation behavior without reading EOF. [disallowed: public API behavior change]
  SuggestedAction: Reject invalid `cursor < 0`, `limit <= 0`, and `maxBytes <= 0` with a 400 response, or clamp to documented minimums. Add route specs for invalid cursor/limit/maxBytes.
  Verification: Existing logs route specs pass, but they only exercise valid query values.
  Status: open

- [ID: item-5]
  Severity: minor
  Scope: packages/server/src/Mohist.Server/Api/LogsRouteContracts.cs
  Evidence: `LogEntryProjection.Project` treats any syntactically valid JSON as a valid `LogRecord` and unconditionally projects `record.Time` and `record.Message` (`LogsRouteContracts.cs:86-94`). A JSON line that is valid but not in the logger's schema, such as `{}` or `{"raw":"x"}`, will deserialize with a default `DateTimeOffset` and possibly a null message, producing a misleading `0001-...` time or a null `message` despite the contract requiring a string. The non-JSON fallback does not cover this case. [disallowed: product behavior change]
  SuggestedAction: Validate parsed records before projecting them. If required fields such as `message` are missing or invalid, degrade to the raw-line element with null structured fields, and add tests for valid JSON that does not match `LogRecord`.
  Verification: Existing tests cover valid logger-shaped JSON and invalid non-JSON lines, but not valid JSON with the wrong shape.
  Status: open

- [ID: item-6]
  Severity: cleanup
  Scope: packages/server/src/Mohist.Server/Logging/FileLoggerProvider.cs; packages/server/src/Mohist.Server/Logging/FileLoggerExtensions.cs; packages/server/src/Mohist.Server/Infrastructure/Hosting/ServiceCollectionExtensions.cs
  Evidence: `FileLoggerProvider` implements `ISingletonService` (`FileLoggerProvider.cs:35`) and is also explicitly registered by `AddFileLogger` as both itself and `ILoggerProvider` (`FileLoggerExtensions.cs:24-28`). Since `AddMohistConventionalServices` scans every `ISingletonService` as self (`ServiceCollectionExtensions.cs:37-39`) and `Program.cs` calls `AddFileLogger()` before `AddMohistServerCore()`, the service collection ends up with duplicate `FileLoggerProvider` self registrations. The logger provider used through `ILoggerProvider` resolves the last self registration today, but the duplicate descriptor is confusing and can create two provider instances if `IEnumerable<FileLoggerProvider>` is ever resolved. [disallowed: registration/architecture cleanup]
  SuggestedAction: Remove the `ISingletonService` marker from `FileLoggerProvider` and let `AddFileLogger` own its explicit registration, or remove the explicit self registration and add a DI test that proves exactly one provider instance is registered and shared with `ILoggerProvider`.
  Verification: Reviewed registration order and scanning behavior; current test suite still passes because no test resolves multiple `FileLoggerProvider` instances.
  Status: open

## Follow-up Items

- [ID: item-7]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Logging/FileLoggerProvider.cs
  Evidence: Log rotation, retention, and size bounding remain out of scope, so `server.log` grows without a retention policy.
  SuggestedAction: Track a follow-up for rotation/retention before this log becomes long-lived operational data.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- None.

<promise>FAIL</promise>
