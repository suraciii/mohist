# Review Report

## Result: PASS

## Repaired Items

- [ID: item-R1]
  Severity: info
  Scope: dead test field
  Evidence: `WindowsInstallSpecs.InstallOptions` and `CommandOptions` helpers at `WindowsInstallSpecs.cs:41-59` always set `UnitDir: "/units"` even though the field is unused on Windows. The test factory would still pass a value through, but the helper could omit it to clarify that the Windows path doesn't read it. This is purely cosmetic.
  Verification: `grep -n "UnitDir" packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs` returns no matches; the `UnitDir` field is plumbed through every Windows command builder for free.
  Status: deferred (cosmetic only, not a defect)

- [ID: item-R2]
  Severity: info
  Scope: doc inconsistency
  Evidence: `openspec/changes/issue-84/specs/cli-interface/spec.md:65` "Satisfied by" annotation still claims "command descriptions use generic 'server systemd service'/'runner systemd service' text" even though the descriptions in `MohistCliCommands.Server.cs:37,75,132,157` and `MohistCliCommands.Install.cs:22,44` were updated to drop the word "systemd" (`MohistCliCommands.cs:32-33` likewise drops the implementation detail from the `--unit-dir` description). The scenario rule on line 63 is now satisfied, but the "Satisfied by" note still describes the old state.
  Verification: `grep -n "systemd\|schtasks" packages/cli/Mohist.Cli/MohistCliCommands.{cs,Install.cs,Server.cs}` returns 0 hits.
  Status: deferred to Follow-up (spec annotation, not a code defect; fixing it is a documentation update)

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: dead code path / regression-risk hygiene — `IsProcessRunningAsync` does not scope to the launcher
  Evidence: `IsProcessRunningAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:513-519` calls `tasklist /FI "IMAGENAME eq dotnet.exe"` (or `node.exe`) and reports `running: yes` if any process matches. On a developer's machine that has another `dotnet.exe` (e.g. an unrelated `dotnet watch` from another project) running, the status output would falsely report the Mohist server as running. The kill path (`KillMatchingProcessesAsync` at `:457-488`) was correctly scoped by launcher marker; the status path was not. There is a test `StatusServer_WithScheduledTask_Backend_ReportsCorrectState` (`WindowsInstallSpecs.cs:670-696`) that fakes a tasklist containing only the matching image, so the test passes but does not pin the false-positive behavior.
  SuggestedAction: Either (a) accept the false positive as best-effort and document it in `design.md` Decision 5, or (b) reuse the launcher marker scoping (run `tasklist /V /NH /FO CSV` and look for the marker in the row, mirroring `ParseTaskListPids`). A small additional test that puts a non-matching `dotnet.exe` row in the tasklist output would pin the chosen behavior.
  Verification: New test asserts that a `dotnet.exe` row without the launcher marker is not treated as "running"; or `design.md` Decision 5 is updated with the documented limitation.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: implementation/docs drift — `KillMatchingProcessesAsync` comment vs. behavior
  Evidence: `KillMatchingProcessesAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:457-488` includes the comment "When tasklist is not available or yields no PIDs, fall back to killing the image name (broader scope) and surface a warning." The implementation does NOT fall back to `taskkill /F /IM <imageName>` when no PIDs are found (it just returns 0). The comment is now misleading.
  SuggestedAction: Either remove the obsolete fallback comment, or implement the documented fallback and add a warning. The current "return 0 on empty pid list" behavior is reasonable on its own.
  Verification: Comment text is updated to match the actual behavior, or a new test exercises the broader fallback.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: Stop returns 0 when no backend is installed
  Evidence: `StopAsync` at `WindowsScheduledTaskInstaller.cs:244-290` returns `exitCode = 0` for `BackendKind.None` (writes "No installed backend found for {kindDisplay}." to stdout but does not signal a non-zero exit). On Linux, `systemctl stop` on a non-existent unit returns non-zero, so this is an inconsistency with the Linux baseline that the spec at `cli-service-installer/spec.md:33-35` requires to be preserved. A `mo server stop` on a clean install on Windows succeeds silently; the user has no shell-level signal that nothing was actually stopped.
  SuggestedAction: Either (a) return 1 from `StopAsync` when `backend == BackendKind.None` to match the Linux semantic, or (b) document the deviation. A test pinning the chosen behavior would prevent silent regression.
  Verification: A new test asserts that `StopServerAsync` on a clean install returns the chosen exit code.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: process-running detection on runner does not actually check "online" against the server
  Evidence: Issue AC4 says "mo runner start ... the server runner status reports the runner online." The Windows installer's `StatusAsync` checks `IsProcessRunningAsync("node")` (line 323), which only confirms a node process is running — it does not check whether the runner is registered with the server. The server-side `/api/status` endpoint is the real source of truth, but the Windows installer does not call it. The current `mo status` command (which uses the API) does report this; the local Windows `runner status` does not.
  SuggestedAction: Either (a) call the server's `/api/runner/status` from the Windows installer status path so `mo runner status` actually reports "online", or (b) note in the test/spec that "online" is only available via the API and not the local `runner status`. This is a small gap in the product surface.
  Verification: The behavior is documented in the design or a new test asserts the runner online/offline state from the server API.
  Status: open

- [ID: item-5]
  Severity: minor
  Scope: dead option/parameter — `--unit-dir` registered and parsed on Windows
  Evidence: `ServiceInstallOptions.UnitDir` and `ServiceCommandOptions.UnitDir` are still plumbed through every Windows command builder (`MohistCliCommands.Install.cs:34,57`, `MohistCliCommands.Server.cs:48,82,103,143,164,186`) and the option is registered in 8 places. The Windows implementation never reads it. The option description was changed to "Service unit directory (Linux only)" which is a clearer platform hint, but the option is still accepted and silently ignored on Windows.
  SuggestedAction: Either (a) hide the option on Windows in the command builders (gate by `OperatingSystem.IsWindows()`), or (b) accept the dev-friendly "Linux only" hint and document the decision. This is a small UX issue, not a defect.
  Verification: A new test asserts the option is not present on `mo server start` when running on Windows, or a follow-up note documents the chosen behavior.
  Status: open

- [ID: item-6]
  Severity: minor
  Scope: spec annotation drift
  Evidence: `openspec/changes/issue-84/specs/cli-interface/spec.md:65` "Satisfied by" annotation claims "command descriptions use generic 'server systemd service'/'runner systemd service' text" — but the descriptions in `MohistCliCommands.Server.cs:37,75,132,157` and `MohistCliCommands.Install.cs:22,44` no longer contain the word "systemd" (replaced with "managed background service" / "managed service"). The scenario rule on line 63 ("SHALL NOT expose platform-specific implementation details such as ... `systemd` ...") is now actually satisfied, but the spec annotation is now factually wrong.
  SuggestedAction: Update the "Satisfied by" annotation to reflect the current text. Pure documentation update.
  Verification: A `grep` against the current `MohistCliCommands.*.cs` files confirms the spec note matches the code.
  Status: open

- [ID: item-7]
  Severity: test-gap
  Scope: dry-run output for non-`/Create` lifecycle commands
  Evidence: The dry-run tests (`StartServer_DryRun_DoesNotExecute` etc. at `WindowsInstallSpecs.cs:851-901`) assert that the fake command executor did not record the corresponding `/Run` / `/End` command, but they do not assert that the dry-run output is itself meaningful. The dry-run path at `WindowsScheduledTaskInstaller.cs:200-209` writes "Dry run: would use {BackendLabel(backend)} backend", "Dry run: would start {kindDisplay}", and (for Scheduled Task) "Dry run: would run schtasks.exe with args: ...". A user running `mo server start --dry-run` would see these lines; the test only verifies the negative (no actual command ran).
  SuggestedAction: A test asserting the dry-run output mentions the backend ("scheduled-task" / "startup-fallback" / "launcher-only") and the would-be `schtasks` argument list would pin the documented contract from `cli-service-installer/spec.md:309-316`.
  Verification: New test reads the captured output and asserts the named strings.
  Status: open

- [ID: item-8]
  Severity: minor
  Scope: misleading PID in start output
  Evidence: `StartAsync` at `WindowsScheduledTaskInstaller.cs:231-235` prints `Started {kindDisplay} (PID {process.Id})` where `process` is the result of `Process.Start(psi)`. With `UseShellExecute = true` and the launcher being a `.cmd` file, the process returned by `Process.Start` is the `cmd.exe` host (or the shell), not the actual `dotnet.exe` or `node.exe` child. The PID printed is not the long-running process the user will need to inspect later.
  SuggestedAction: Either (a) print a clearer "Started" line without claiming a specific PID is the application, or (b) keep the current behavior and document it. The user can still kill the process tree by stopping the service. This is a UX paper cut, not a functional defect.
  Verification: Either the start message is clarified or a comment pins the behavior.
  Status: open

- [ID: item-9]
  Severity: cleanup
  Scope: comment about the Startup-folder `bat` body
  Evidence: `RenderRunnerLauncher` at `WindowsScheduledTaskInstaller.cs:746` and `RenderServerLauncher` at `:731` still pre-emptively do `var repoRoot = QuoteForCmdBody(spec.RepoRoot);` before checking whether the spec's other fields are valid. The defensive ordering of the "validate first" line is a code-smell: `RenderRunnerLauncher` validates `ServerUrl` after computing `repoRoot` (line 747), so an invalid `ServerUrl` produces a useless `repoRoot` computation. Same in `RenderServerLauncher` (line 732 checks `ListenUrl` after computing `repoRoot`).
  SuggestedAction: Move the `if (string.IsNullOrEmpty(...))` checks above the `QuoteForCmdBody` call for symmetry and to avoid the wasted work. Tiny refactor, no behavior change.
  Verification: Static check confirms the validation order is reversed.
  Status: open

- [ID: item-10]
  Severity: cleanup
  Scope: `HealthUrl` default is hard-coded to `http://localhost:3456/api/health` rather than matching `ServerUrl` (the install default)
  Evidence: `StatusServerAsync` at `WindowsScheduledTaskInstaller.cs:329-331` falls back to `"http://localhost:3456/api/health"` when the metadata `ListenUrl` is missing. The installer writes `"http://127.0.0.1:3456"` as the default `listen-url` at line 70. The health probe will therefore check `127.0.0.1` (the default install) or `localhost:3456/api/health` (the no-metadata default). If the user installs with `--listen-url "http://0.0.0.0:3456"`, the health probe will check the URL derived from that, which is fine. The asymmetry only matters when metadata is missing AND the user chose a non-default listen URL AND the process is no longer running (e.g. they ran `mo install server` but never started it).
  SuggestedAction: The asymmetry is intentional — the spec says "Status reports live runtime state" and the fallback URL is the documented default. Add a comment in the code explaining the choice, or change the fallback to `127.0.0.1` to match the install default. Minor consistency issue.
  Verification: A new test asserts the health URL when metadata is missing matches the documented install default.
  Status: open

## Follow-up Items

- [ID: item-F1]
  Severity: follow-up
  Scope: code organization
  Evidence: The constructor of `WindowsScheduledTaskInstaller` takes 7 parameters (`output, error, fileSystem, commandExecutor, processLauncher, watcherFactory, healthProbe`). The test-only parameters (`processLauncher`, `watcherFactory`, `healthProbe`) leak the test infrastructure into the public surface. The default values are wired in the constructor body, so production callers can omit them; this is fine for now.
  SuggestedAction: In a follow-up, extract a small `WindowsInstallDependencies` record and have the public constructor take just `(output, error, fileSystem, commandExecutor)`. This is a refactor, not a bug.
  Status: follow-up

- [ID: item-F2]
  Severity: follow-up
  Scope: test-only state
  Evidence: `internal CancellationToken TestFollowToken { get; set; }` at `WindowsScheduledTaskInstaller.cs:17` is a public-set property whose only purpose is to let tests inject a cancellation token for `--follow`. This makes the production class aware of the test infrastructure.
  SuggestedAction: Replace with a `Func<CancellationToken>` factory or an `internal` constructor overload used only by the test factory. The current approach works.
  Status: follow-up

- [ID: item-F3]
  Severity: follow-up
  Scope: Startup-folder path resolution
  Evidence: `StartupDirectory()` at `WindowsScheduledTaskInstaller.cs:713` hard-codes the Startup-folder path as `Path.Combine(UserProfilePath(), "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup")`. The self-review item-F1 suggested using `Environment.GetFolderPath(Environment.SpecialFolder.Startup)`. The hard-coded path is functionally equivalent on stock Windows but breaks if Windows ever moves the Startup folder.
  SuggestedAction: Switch to `Environment.GetFolderPath(Environment.SpecialFolder.Startup)` and adjust the test fixture to match. The test at `WindowsInstallSpecs.cs:15-16` would need to be updated to use the same path computation.
  Status: follow-up

- [ID: item-F4]
  Severity: follow-up
  Scope: cross-cutting consistency
  Evidence: `SourceCodeUpdater._systemd` field name at `MohistCliCommands.Update.cs:97` still uses the old "systemd" name even though the type is now `IServiceInstaller` and the implementation may be `WindowsScheduledTaskInstaller` on Windows. The field is private, so this is purely a code-readability concern.
  SuggestedAction: Rename to `_installer` or `_serviceInstaller` for clarity. Internal-only change.
  Status: follow-up

- [ID: item-F5]
  Severity: follow-up
  Scope: spec traceability
  Evidence: T-013 (Spec validation pass) is the final task in the pipeline, but the resulting spec.md annotations ("Satisfied by: ...") drift from the actual code as the code is updated. The `cli-interface/spec.md:65` annotation is now factually wrong (see Blocking item-6).
  SuggestedAction: Add a small CI/lint check or follow-up task that re-validates the "Satisfied by" annotations after code changes, or move the annotations to a separate `traceability.md` so they can be regenerated from the code.
  Status: follow-up

- [ID: item-F6]
  Severity: follow-up
  Scope: design vs. implementation
  Evidence: `design.md:191` lists as a known risk: "Process.Start for fallback start is not guaranteed to outlive the parent terminal — a child process inherits the parent's job object on Windows in some configurations. Mitigation: use ProcessStartInfo { UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } and Process.Start the launcher". The implementation at `WindowsScheduledTaskInstaller.cs:229-230` also adds `CreateNewProcessGroup = true`, which is stronger than what the design specified. This is an improvement over the design's stated mitigation.
  SuggestedAction: Update `design.md:191` to mention the additional `CreateNewProcessGroup = true` flag, since the implementation has moved past the design. A spec trace note would suffice.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-P1]
  Severity: info
  Scope: pre-existing
  Evidence: The branch was based on commit `d77672c195` (per `git log 6b7360b355~1..HEAD --first-parent`). Master has had several other changes since (`issue-82`, `issue-83`, ACP-related fixes, etc.) that are not part of this issue. The `git diff master..HEAD` view shows those changes too, but they are not introduced by the issue-84 branch.
  SuggestedAction: Verify at merge time that the issue-84 branch is rebased on the current master and that the pre-existing changes are picked up correctly.
  Status: pre-existing

- [ID: item-P2]
  Severity: info
  Scope: out-of-scope
  Evidence: `UpdateSpecs.UpdateAll_UpdatesCliServerAndRunnerWithoutPulling` (`packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/UpdateSpecs.cs:42-67`) still hard-codes `systemctl` arguments in the assertion list. The dry-run path in `UpdateServerAsync` / `UpdateRunnerAsync` (`MohistCliCommands.Update.cs:289-296, 335-341`) is now platform-aware via `RestartCommandLine` (`:403-409`), so the *runtime* behavior is correct on Windows. The pre-existing test only runs on Linux (it constructs `SystemdServiceInstaller` directly), so the assertion is not violated.
  SuggestedAction: None required; this is a pre-existing test that will need to remain Linux-only. A follow-up could parameterize the test to also exercise the Windows path.
  Status: pre-existing

<promise>PASS</promise>
