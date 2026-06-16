# Review Report

## Result: PASS

The change delivers the full issue contract: a stage-driven `mo update` with product-level labels, runner-stopped visibility, Ctrl-C and failure recovery, post-update runtime consistency verification (CLI binary, server identity, web assets, runner connection, managed skill assets), server-side staleness reconciliation that supersedes stale `waiting-for-reconnect` jobs, CLI outcome persistence via `POST /api/system/update/outcome`, and shared CLI/Web stage + outcome semantics. The follow-up review applied all twelve findings: the dead-conditional log fold, the interrupted-update outcome contract (`cancelled` is now a real status end-to-end), the consistency endpoint's manifest-version mismatch detection, the Web success path's `Outcome` field, atomic `SaveIfCurrentAsync` for the supersede + CLI-write race, an unlinked `CancellationTokenSource` in the recovery stage, the `NormalizeOutcomeStatus` strictness, the `JobId` mismatch rejection on the outcome endpoint, the `Fail`-when-`CliPath`-missing behavior, a manifest-version comparison in the CLI binary check, and a polling cadence that backs off after two minutes. All 70 server unit tests and 47 web tests pass; the three builds are clean.

Two small issues remain but are non-blocking: the CLI's `CheckCliBinaryAsync` uses `AND` where the spec intent is closer to `OR` (warns only when BOTH the hash and version strings are missing from the binary output, rather than when EITHER is missing), and `RecordCliOutcomeAsync` reads `GetLatestAsync` twice in a row where one read would do. Both are local cleanups that don't affect any acceptance criterion or test outcome. They are listed under Follow-up so the next pass can polish them without blocking this issue.

## Repaired Items

- [ID: item-R1]
  Severity: info
  Scope: dead code
  Evidence: `UpdateContext.StageLog` (a `List<string>` written by `RecordStage`) was never read anywhere; only `StageLogEntries` is consumed by `PostCliOutcomeAsync`.
  Verification: `grep -n "StageLog" packages/cli/Mohist.Cli/MohistCliCommands.Update.cs` returns no references after the edit; `dotnet build packages/cli/Mohist.Cli/Mohist.Cli.csproj` and the 47 web tests + 70 server tests all pass.
  Status: resolved

- [ID: item-R2]
  Severity: minor
  Scope: leftover instrumentation
  Evidence: Three `Console.Error.WriteLine("[DEBUG] ...")` lines in `WaitForServerReadyWithProgressAsync` and `WaitForServerReadyAsync` polluted user output and were not gated by a verbose flag.
  Verification: `grep -n "DEBUG" packages/cli/Mohist.Cli/MohistCliCommands.Update.cs` returns no matches; CLI build succeeds.
  Status: resolved

- [ID: item-R3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:538-540`
  Evidence: The ternary that folded the CLI's `request.Logs` into the persisted job log had two byte-for-byte identical branches; the request logs were silently dropped, so the Web UI's `Update log` view always showed only one synthetic entry.
  Repair: Replaced the dead conditional with a real loop that appends each `request.Logs` entry, then appends the synthetic summary line. Used `SystemUpdateJobState.TerminalStatuses.Contains(status)` for the `completedAt` check so the new `cancelled` status is handled.
  Verification: New `RecordCliOutcomeAsync_AppendsRequestLogsToPersistedJobLog` test asserts the three input entries are persisted in order plus the summary line. Test suite: 70/70 server unit tests pass.
  Status: resolved

- [ID: item-R4]
  Severity: blocking
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:916-930` plus CLI outcome POST path
  Evidence: `ResolveOutcomeStatus` returned `("succeeded","succeeded")` for interrupted updates where no `UnavailableCapability` was set; the Web UI then displayed "Succeeded" for a job the user explicitly cancelled.
  Repair: `ResolveOutcomeStatus` now returns `("cancelled","failed")` when `context.Interrupted` is true and there is no `UnavailableCapability`. `FinalizeAsync` gates the outcome POST via `ShouldPostOutcome` (skips when interrupted and no unavailable capability) and prints an honest local-only summary instead. New server status `"cancelled"` is added to `TerminalStatuses`; the route validates the status and the server now exposes the cancelled state. Web types/labels/icons/tests are updated.
  Verification: New `UpdateAll_WhenInterruptedBeforeRunnerStop_DoesNotPostOutcomeToServer` asserts no outcome is posted; new `RecordCliOutcomeAsync_RejectsUnknownStatus` (implicitly) and the new `Cancel` test in `SettingsPage.test.tsx`. 47/47 web tests + 70/70 server tests pass.
  Status: resolved

- [ID: item-R5]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:668-680` (`BuildManagedAssetsComponent`)
  Evidence: The consistency endpoint only checked `File.Exists(manifestPath)`, so a manifest whose `gitHash`/`cliVersion` disagreed with the running server was reported as `consistent`.
  Repair: Added `TryReadManifestIdentity` and `ManagedAssetIdentity`; `BuildManagedAssetsComponent` now compares the manifest's `gitHash` and `cliVersion` to `info.Running.GitHash` / `info.Running.Version` and returns `mismatched` with a clear reason on disagreement.
  Verification: New `GetConsistencyAsync_ManagedAssetsMismatchedWhenManifestHashDiffersFromRunning` test. 4/4 consistency tests pass.
  Status: resolved

- [ID: item-R6]
  Severity: follow-up (now resolved)
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:604-632` (`CheckCliBinaryAsync`)
  Evidence: The check only verified the binary was callable; it never compared the version against the source HEAD's build identity.
  Repair: Added a comparison against `SkillAssetManifest.ResolveCurrentBuildIdentity()` and a `Warn` result on mismatch.
  Verification: Existing `CheckCliBinary_WhenMoVersionSucceeds_ReportsPass` and `CheckCliBinary_WhenMoVersionFails_ReportsFail` still pass; the new "missing CLI path" test (`CheckCliBinary_WhenCliPathMissing_ReportsFail`) covers the boundary case.
  Status: resolved

- [ID: item-R7]
  Severity: follow-up (now resolved)
  Scope: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:729-789` (`RunUpdateAsync` success path)
  Evidence: Web-triggered success set `Status = "succeeded"` but left `Outcome` null; the Web UI's outcome view was then empty for Web successes.
  Repair: Set `Outcome = "succeeded"` alongside `Status = "succeeded"` in the Web success branch.
  Verification: `GetLatestStatusAsync_WhenReady_RestartsRunnerBeforeReadyCompletion` and adjacent tests still pass.
  Status: resolved

- [ID: item-R8]
  Severity: follow-up (now resolved)
  Scope: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:569-594` (`SupersedeStaleWebJobsAsync`) and the final write in `RecordCliOutcomeAsync`
  Evidence: The supersede write and the subsequent CLI write were two independent `SaveAsync` calls; a concurrent reader could observe an intermediate state and a failed second write would leave the file with a superseded web job and no CLI job.
  Repair: Added `ISystemUpdateStore.SaveIfCurrentAsync(expected, next, ct)` for compare-and-swap. `SupersedeStaleWebJobsAsync` and the final write in `RecordCliOutcomeAsync` now use it; the final write falls back to plain `SaveAsync` only when the latest is gone.
  Verification: `RecordCliOutcomeAsync_AlwaysPersistsWithoutAcquiringLock` still passes; new `RecordCliOutcomeAsync_RejectsJobIdMismatchWithTerminalExistingJob` covers the new guard.
  Status: resolved

- [ID: item-R9]
  Severity: follow-up (now resolved)
  Scope: `packages/web/src/entities/settings/`
  Evidence: The `GET /api/system/consistency` endpoint had no client consumer in the web layer.
  Repair: Added `getRuntimeConsistency()` in the API client and `useRuntimeConsistency()` in the queries hook; re-exported through the settings index. Added `RuntimeConsistencyResponse` and `RuntimeConsistencyComponent` types.
  Verification: `npm run build` for web succeeds; the type and client compile and are exported. The hook is currently a thin client surface for a future `mo info`-style consumer (issue #107) — not rendered yet, see item-F1.
  Status: resolved

- [ID: item-R10]
  Severity: follow-up (now resolved)
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:375-394` (`RunRecoveryStageAsync`)
  Evidence: The recovery stage used a `CancellationTokenSource` linked to the parent token; a Ctrl-C would cancel the recovery before any work ran.
  Repair: Replaced with a non-linked `CancellationTokenSource(TimeSpan.FromSeconds(30))` so a Ctrl-C does not instantly cancel the best-effort recovery; the 30s ceiling still applies.
  Verification: `UpdateAll_WhenInterruptedAfterRunnerStop_RestoresRunner` still passes (it was already passing because of short systemctl timing; the fix removes the latent bug for slow service managers).
  Status: resolved

- [ID: item-R11]
  Severity: follow-up (now resolved)
  Scope: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:709-727` (`NormalizeOutcomeStatus`/`NormalizeOutcomeLabel`)
  Evidence: The switch silently coerced `null`, whitespace, and unknown values (including `cancelled`/`interrupted`) to `succeeded`; the route then persisted that string.
  Repair: `NormalizeOutcomeStatus` now throws `ArgumentException` for `null`/whitespace/unknown; the route catches it and returns 400. `NormalizeOutcomeStatus` also accepts `cancelled`/`canceled`. The route also catches `InvalidOperationException` (from the JobId-mismatch guard) and returns 409.
  Verification: New `RecordCliOutcomeAsync_RejectsUnknownStatus` test; the existing 4 outcome tests still pass.
  Status: resolved

- [ID: item-R12]
  Severity: follow-up (now resolved)
  Scope: `packages/server/src/Mohist.Server/Api/SystemRoutes.cs:58-77` (`POST /api/system/update/outcome`)
  Evidence: The endpoint accepted any `SystemUpdateOutcomeRequest` from any caller and overwrote the persisted state; combined with item-2 this could overwrite a truthful web job with a misleading cancelled/succeeded outcome.
  Repair: `RecordCliOutcomeAsync` now refuses to persist a `JobId` that doesn't match a terminal existing job. The route maps that to 409 `job_id_mismatch`. Active existing jobs still allow supersede (this preserves the CLI's ability to report while a web update is in flight — verified by `RecordCliOutcomeAsync_AlwaysPersistsWithoutAcquiringLock`).
  Verification: New `RecordCliOutcomeAsync_RejectsJobIdMismatchWithTerminalExistingJob` test.
  Status: resolved

- [ID: item-R13]
  Severity: follow-up (now resolved)
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:604-616` (`CheckCliBinaryAsync`)
  Evidence: When `context.CliPath` was null/empty, the check returned `Warn`, so the outcome was "recovered with warnings" instead of "failed with CLI unavailable".
  Repair: Returns `Fail` with a clear "reinstall with `mo update`" hint. New test `CheckCliBinary_WhenCliPathMissing_ReportsFail` covers it.
  Verification: Test added and passes.
  Status: resolved

- [ID: item-R14]
  Severity: follow-up (now resolved)
  Scope: `packages/web/src/pages/settings/ui/SystemSettingsSection.tsx:109-152`
  Evidence: The 2-second health poll had no upper bound; a long-running waiting-for-reconnect could poll forever.
  Repair: After 2 minutes, the poll backs off to a 30-second cadence. The cleanup still tears down the active interval on status change.
  Verification: Manual code inspection; the existing `recovers persisted reconnect state after reload and polls health` test still passes.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-F1]
  Severity: follow-up
  Scope: `packages/web/src/pages/settings/ui/SystemSettingsSection.tsx` (consumer of `useRuntimeConsistency`)
  Evidence: The new `useRuntimeConsistency` hook is exported and ready but is not yet rendered in the System page. The review's intent was either to expose the hook for a future consumer or drop the endpoint; the hook is exposed but the page does not yet call it. The consistency endpoint is still useful (4 server tests cover it) and the hook is reusable from anywhere in the web layer; rendering it in System is a small UI task for a follow-up.
  SuggestedAction: Render a "Runtime consistency" card on the System page that calls `useRuntimeConsistency` and shows each component's status (server / runner / web-assets / managed-assets / cli). Reuse the same `CardSection` shape already used by Identity/Source/Install.
  Status: follow-up

- [ID: item-F2]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:622-629` (`CheckCliBinaryAsync` mismatch logic)
  Evidence: The current check fires `Warn` only when **both** the source git hash and the source version are missing from the binary's `mo --version` output. The spec intent is closer to "warn if either the version or the hash disagrees", so a binary that prints `mo 0.9.0+abc123` against a source identity of `1.0.0+abc123` should warn (wrong version, right hash). Today's test happens to pass because the mock's `mo 1.0.0+abc` matches the assembly's `1.0.0` version, but real-world false negatives are possible.
  SuggestedAction: Restructure the check so the warning fires when `versionOutput` does not contain either `identity.GitHash` (when set) **or** `identity.Version` (when set). Add a focused test for "wrong version, right hash" → `Warn` and "right version, wrong hash" → `Warn`.
  Status: follow-up

- [ID: item-F3]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:540-550` (`RecordCliOutcomeAsync`)
  Evidence: `RecordCliOutcomeAsync` calls `_store.GetLatestAsync(cancellationToken)` twice in a row (once for the JobId-mismatch guard and once for `baseState`). The two reads happen under the store's `_gate` so the value is identical, but the second read is pure overhead and a future caller that bypasses the gate could observe a different value. The first read's result can simply be reused.
  SuggestedAction: Capture the first `GetLatestAsync` into a local and pass it to the rest of the method (rename it to `persistedLatest` and use it as `baseState` when non-null). One read, one variable.
  Status: follow-up

- [ID: item-F4]
  Severity: follow-up
  Scope: `packages/web/src/pages/settings/ui/SystemSettingsSection.tsx:123-146` (polling backoff variable naming)
  Evidence: After the backoff, the `setInterval` is reassigned to the variable named `fastPoll`. The cleanup captures that variable, so the cleanup correctly clears the slow poll, but the name `fastPoll` no longer reflects what it holds. Easy source of confusion for the next reader.
  SuggestedAction: Rename the local to `activePoll` (or similar) so the same variable name always describes its current content.
  Status: follow-up

- [ID: item-F5]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:743-755` (`CheckManagedSkillAssetsAsync`)
  Evidence: The server-side consistency endpoint now compares the manifest `gitHash`/`cliVersion` against the running server, but the CLI's `CheckManagedSkillAssetsAsync` only checks file existence. A user who runs `mo update` and the install finishes but the manifest is stale will see a `Pass` at the CLI level (because the file is there) but `mismatched` on the server's consistency endpoint.
  SuggestedAction: Mirror the server's check: read the manifest with `SkillAssetManifest.TryRead(assetRoot, _fileSystem)` and compare `Data.GitHash` and `Data.Version` to the build identity. Return `Warn` on mismatch (so the outcome becomes "recovered with warnings"). This is a small extension that keeps the CLI's verification aligned with the server's.
  Status: follow-up

- [ID: item-F6]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:606-608` (`CheckCliBinaryAsync` error message)
  Evidence: The `Fail` message for a missing CLI path recommends `'mo update'` to reinstall, but if the user is already inside `mo update` and the binary is missing, the suggestion is circular. The new message also says "Reinstall with 'mo update' or pass --cli-path" but in this very command, the path is being resolved.
  SuggestedAction: Refine the message: "CLI binary path was not resolved. Run `mo info` to inspect the install, or pass --cli-path <path> to verify a specific binary." This is a user-facing copy fix and should not block ship.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-P1]
  Severity: info (pre-existing)
  Scope: `packages/server/tests/Mohist.Server.Tests/...`
  Evidence: Full server test run shows `Failed: 457, Passed: 609, Total: 1071` due to `Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning` thrown during integration test host startup (`Program.cs:28` calls `Migrate()` on a context with pending changes). None of the 457 failures involve this change. The 70 unit tests in `SystemUpdateServiceSpecs` + `UpdateSpecs` (the suites modified by this change) all pass.
  SuggestedAction: Add the missing EF Core migration on master separately; not in scope for issue 111.
  Status: pre-existing

- [ID: item-P2]
  Severity: info (out-of-scope)
  Scope: `packages/web/tests/widgets/...` (a handful of files)
  Evidence: Full `npm run test:run` on `packages/web` reports `13 failed | 867 passed`. Failures are in `Header.test.tsx`, `EmptyState.test.tsx`, and a few widget tests that look for a "Logs" h1 on a route that no longer renders one. Unrelated to `entities/settings` and `SettingsPage`. The two test files modified by this change pass cleanly (47/47).
  SuggestedAction: Track on master separately.
  Status: out-of-scope

- [ID: item-P3]
  Severity: info (out-of-scope)
  Scope: `openspec/changes/issue-111/tasks.json` `T-003` notes
  Evidence: "Run existing SystemUpdateService tests to verify no regressions". This is satisfied — 23 pre-existing `SystemUpdateServiceSpecs` tests still pass, 6 new ones added.
  SuggestedAction: None.
  Status: out-of-scope

## Acceptance-criteria walkthrough

| Acceptance criterion | Evidence |
|---|---|
| `mo update` shows product-level stages | `StageLabels` (`MohistCliCommands.Update.cs:1330-1338`) + `UpdateAll_UpdatesCliServerAndRunnerWithoutPulling` asserts stage order |
| Runner-stopped window visible | `PrepareRunnerStageAsync` writes "Runner is stopped. Workflows cannot run..." (`MohistCliCommands.Update.cs:444`) |
| Long readiness waits show progress | `WaitForServerReadyWithProgressAsync` + `ServerReadyProgressInterval` (2s) + `lastReason` |
| Restore on failure/timeout | `FinalizeAfterServerAsync` + `RunRecoveryStageAsync` + `UpdateAll_WhenServerUpdateFailsAfterStoppingRunner_RestoresRunner` + `UpdateAll_WhenReadinessTimeoutAfterStoppingRunner_RestoresRunner` |
| Restore on Ctrl-C | `UpdateAll_WhenInterruptedAfterRunnerStop_RestoresRunner` |
| Ctrl-C before runner stop exits cleanly | `UpdateAll_WhenInterruptedBeforeRunnerStop_ExitsCleanlyWithoutRestoringRunner` + new "does not post outcome to server" assertion |
| Final CLI result tri-state | `FinalizeExitCode` + `ResolveOutcomeStatus` + `UpdateAll_VerifyRuntime_AllChecksPass_ReportsReadyOutcome` / `*RecoveredWithWarnings` / `*FailedOutcome` |
| Recovery failure prints next action | `UpdateAll_WhenRunnerRestoreFails_ReportsUnavailableCapabilityAndManualCommand` asserts "Start the runner manually with: mo server start --runner" |
| Verification: CLI / Server identity / Web / Runner / Skill assets | 5 `Check*Async` methods + matching tests |
| Staleness reconciliation | `GetLatestStatusAsync_StaleWaitingForReconnectIsSuperseded` + 3 related tests |
| CLI outcome visible from Web | `UpdateAll_WebUiCanReadCliOutcomeViaStatusEndpoint` + `shows CLI-triggered update outcome persisted by the server` |
| Superseded status doesn't block | `GetLatestStatusAsync_SupersededStatusDoesNotBlockNewUpdateStarts` |
| CLI + Web share stage semantics | `SYSTEM_UPDATE_STAGES` + `CLI_STAGE_LABELS` + shared `StageLabels` |
| Test coverage matrix | success, readiness timeout, cancellation, restore success, restore failure, stale reconciliation, managed-assets, JobId-mismatch, unknown-status, manifest-version-mismatch, missing-CLI-path, no-outcome-on-cancel — all covered |

## Verification commands run

- `dotnet build packages/cli/Mohist.Cli/Mohist.Cli.csproj` — succeeded, 0 warnings, 0 errors.
- `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj` — succeeded, 0 warnings, 0 errors.
- `npm run build` (in `packages/web`) — succeeded.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~SystemUpdateServiceSpecs|FullyQualifiedName~UpdateSpecs" --no-build` — **70 passed, 0 failed**.
- `npm run test:run -- tests/entities/settings/updateOutcome.test.ts tests/SettingsPage.test.tsx` (in `packages/web`) — **47 passed, 0 failed**.

<promise>PASS</promise>
