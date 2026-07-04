# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test coverage
  Evidence: The new disabled-start specs asserted that `TryAcquireLockAsync("job-next")` succeeded after `StartAsync`, which proved the fake lock was free after the call but did not strictly prove the disabled path made zero lock acquisition attempts. Added `InMemoryUpdateStore.AcquireAttempts` and `Assert.Equal(0, store.AcquireAttempts)` to both disabled-path specs before the intentional post-call lock probe (`packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs:222`, `:253`, `:1805`, `:1819`).
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true --filter FullyQualifiedName~SystemUpdateServiceSpecs` passed: Failed 0, Passed 54, Skipped 0, Total 54.
  Status: resolved

## Blocking Items

None.

Acceptance evidence reviewed:

- `SystemUpdateService.IsUpdateEnabled` is explicit control flow with a non-blank presence check, `bool.TryParse(...) && enabled`, and separate `return true` default (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:605-616`).
- `ValidateStart` ordering is unchanged: install mode, enable gate, install completeness, dirty source, update availability (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:585-600`).
- `Enabled="false"` start path returns `update_disabled`, no status, expected error text, no commands, no saved states, and no lock acquisition attempts (`packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs:203-223`).
- Disabled gate precedence over dirty-source and no-update-available is covered (`packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs:228-254`).
- Explicit `true` does not reject at the enable gate (`packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs:257-275`).
- Unconfigured, null, empty, and whitespace `Enabled` preserve the default-enabled start path and still run the existing fixed update commands (`packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs:278-319`).
- Source audit guards against the old precedence-dependent single-line expression and requires the explicit structure (`packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemUpdateServiceSpecs.cs:1615-1633`).
- Display-path parity evidence remains present for unconfigured, explicit false, and explicit true (`packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/SystemInfoServiceSpecs.cs:47-76`, `:81-110`, `:316-344`).
- Product diff is limited to `SystemUpdateService.cs` and `SystemUpdateServiceSpecs.cs`; no public contract, storage, migration, Web, Runner, or CLI behavior changes were introduced.

Verification:

- `dotnet test Mohist.sln -p:SkipWebBuild=true --filter FullyQualifiedName~SystemUpdateServiceSpecs` passed: Failed 0, Passed 54, Skipped 0, Total 54.
- `npm test` post-repair completed server and web successfully: server Failed 0, Passed 3774, Skipped 13, Total 3787; web Test Files 263 passed, Tests 4141 passed and 1 skipped.
- `npm test` post-repair did not complete green because of the out-of-scope runner timeout recorded below.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: packages/runner/tests/runner-host-task-log.spec.ts
  Evidence: The post-repair `npm test` run timed out in `RunnerHost task-log best-effort flush (T-003) > RoutesConcurrentIncrementalUploadsToEachWorkItemCollector` after server and web had already passed. The same full `npm test` command passed before the local server-spec repair, the candidate diff does not touch runner files, and isolated reruns passed: `npm test -w packages/runner -- tests/runner-host-task-log.spec.ts -t RoutesConcurrentIncrementalUploadsToEachWorkItemCollector` passed 1 test, and `npm test -w packages/runner -- tests/runner-host-task-log.spec.ts` passed 15 tests.
  SuggestedAction: Track separately as runner test flakiness if it recurs under full-suite load.
  Status: out-of-scope

<promise>PASS</promise>
