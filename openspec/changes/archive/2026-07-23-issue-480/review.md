# Review — issue-480

## Findings

No issues found. The current implementation now:

- limits `runner` to `list`, `view`, and `status`, with remote Server-backed reads only;
- routes local lifecycle through `service <verb> <server|runner>`;
- keeps `server` read-only and separates application logs from service-manager logs;
- removes the retired command paths and updates the remaining Runner recovery hint to `mo service start runner`.

## Verification

- `dotnet build Mohist.sln --no-restore` passed with zero warnings and errors.
- CLI command specs passed: 42 tests in `CliRunnerCommandSpecs`.
- Server recovery specs passed: 5 tests in `SystemUpdateFailureRecoverySpecs`.
- The full CLI suite passed: 1410 tests.
- The full Server spec suite still has one known unrelated inbox-dispatch timeout, consistent with the pre-existing flaky test recorded before issue 480.

<promise>PASS</promise>
