# Design: Cross-platform managed service install (issue-84)

## Context

Mohist's `mo` CLI currently exposes a managed service surface (`mo install server|runner`, `mo server|runner start|stop|restart|status|logs|uninstall`) that is wired straight to `SystemdServiceInstaller`. That class generates a user-systemd unit file under `~/.config/systemd/user/` and calls `systemctl --user`. On Windows there is no equivalent code path, so Windows developers have to start the server (`dotnet run --project packages\server\src\Mohist.Server\Mohist.Server.csproj`) and the runner (`node packages\runner\dist\cli.js`) by hand in terminal windows; closing those windows stops Mohist, and there is no login auto-start.

The current code lives in:

- `packages/cli/Mohist.Cli/SystemdServiceInstaller.cs` — generates units, runs `systemctl`, tails `journalctl`, returns structured exit codes; still used by `SourceCodeUpdater` to restart server/runner after `dotnet build` / `npm run build`.
- `packages/cli/Mohist.Cli/MohistCliCommands.{Install,Server,Update}.cs` and `MohistCliCommands.cs` — resolve `SystemdServiceInstaller` directly from DI and pass it to the command builders.
- `packages/cli/Mohist.Cli/Program.cs` — constructs `SystemdServiceInstaller` against the real `IFileSystem`/`ICommandExecutor` and registers it in DI.
- `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/InstallSpecs.cs` and `UpdateSpecs.cs` — pin the Linux install/render baseline.

Issue 84 requires the same command surface to work on Windows using a Hermes-shaped backend: per-user Scheduled Task with `/SC ONLOGON /RL LIMITED`, generated `.cmd` launcher, and a Startup-folder `.cmd` fallback when Task Scheduler access is denied.

## Goals / Non-Goals

**Goals**

- Replace the hard-coded `SystemdServiceInstaller` dependency in command builders with a platform-neutral `IServiceInstaller` interface covering install / start / stop / restart / status / logs / uninstall for both server and runner.
- Keep the Linux code path byte-identical to today (generated unit content, `systemctl --user` invocation, dry-run output, install paths) so `InstallSpecs` / `UpdateSpecs` / `SystemdInstallDetectorSpecs` keep passing.
- Add `WindowsScheduledTaskInstaller` that renders a launcher `.cmd`, registers `Mohist_Server` / `Mohist_Runner` Scheduled Tasks via `schtasks /Create /SC ONLOGON /RL LIMITED`, and falls back to a Startup-folder `.cmd` shortcut that re-invokes the generated launcher.
- Honor `--dry-run` everywhere: no file writes, no `schtasks` / `systemctl` / `Process.Start` side effects on either platform.
- Quote Windows paths and arguments safely — `.cmd` body and `schtasks /TR` go through two distinct helpers, both of which take argument arrays (no shell-string concatenation).
- Test the new Windows path on its own (no Windows CI required): tests assert the exact `schtasks` argument list, the launcher / fallback content, dry-run output, and uninstall behavior.

**Non-Goals**

- True Windows Service / SCM registration (`sc.exe`, WinSW, ASP.NET Core `UseWindowsService()`).
- Auto-installing `dotnet` / Node / opencode.
- Changing the server or runner runtime beyond the launcher invocation.
- A Windows service management GUI.
- Solving crash-recovery / orphan-process cleanup beyond the existing best-effort stop+start.
- Touching `~/.mohist/mohist.db`, `config.jsonc`, project worktrees, or `out.log` during uninstall.

## Decisions

### 1. Introduce `IServiceInstaller` and select implementation at DI build time

`IServiceInstaller` lives in `packages/cli/Mohist.Cli/IServiceInstaller.cs` and declares the methods currently on `SystemdServiceInstaller` plus a couple of platform-agnostic queries the new Windows path needs:

```csharp
internal interface IServiceInstaller
{
    Task<int> InstallServerAsync(ServiceInstallOptions options);
    Task<int> InstallRunnerAsync(ServiceInstallOptions options);

    Task<int> StartServerAsync(ServiceCommandOptions options);
    Task<int> StopServerAsync(ServiceCommandOptions options);
    Task<int> RestartServerAsync(ServiceCommandOptions options);
    Task<int> StatusServerAsync(ServiceCommandOptions options);
    Task<int> LogsServerAsync(ServiceCommandOptions options);
    Task<int> UninstallServerAsync(ServiceCommandOptions options);

    Task<int> StartRunnerAsync(ServiceCommandOptions options);
    Task<int> StopRunnerAsync(ServiceCommandOptions options);
    Task<int> RestartRunnerAsync(ServiceCommandOptions options);
    Task<int> StatusRunnerAsync(ServiceCommandOptions options);
    Task<int> LogsRunnerAsync(ServiceCommandOptions options);
    Task<int> UninstallRunnerAsync(ServiceCommandOptions options);
}
```

`ServiceInstallOptions` / `ServiceCommandOptions` are promoted from `internal sealed` (inside `SystemdServiceInstaller.cs`) to top-level internal records in their own files (`ServiceInstallOptions.cs`, `ServiceCommandOptions.cs`) so the Windows implementation can read the same options. The `From(string[])` factory and the `--lines/-n` / `--follow` parsing stay unchanged — that is what every command builder already calls.

Composition root picks the implementation once, in both `Program.cs` and `MohistCliCommands.RunAsync` (the test entry point):

```csharp
services.AddSingleton<IServiceInstaller>(sp =>
    OperatingSystem.IsWindows()
        ? new WindowsScheduledTaskInstaller(out, err, files, executor)
        : new SystemdServiceInstaller(out, err, files, executor));
```

`SystemdServiceInstaller` is changed to `internal sealed class SystemdServiceInstaller : IServiceInstaller`. Its public method surface stays the same so existing tests keep working without rewrites. `SourceCodeUpdater` swaps its `SystemdServiceInstaller` field for `IServiceInstaller` (constructor change only — every call site is already an interface method).

`MohistCliCommands.Install.cs` and `MohistCliCommands.Server.cs` resolve `IServiceInstaller` instead of `SystemdServiceInstaller` and pass it to the existing builders.

**Alternatives considered**

- *Static factory class with platform branches*: rejected because it keeps the platform split inside one class and makes Windows-only test assertions awkward. Splitting implementations behind a single interface makes the Linux path a black box to the Windows specs.
- *Two separate DI registrations + runtime switch inside command builders*: rejected because the spec requires command builders to contain *no* platform-specific code paths (`cli-service-installer/spec.md:25`).

### 2. `WindowsScheduledTaskInstaller` is a sibling of `SystemdServiceInstaller`, not a child

The new class lives at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs`, takes the same `(TextWriter, TextWriter, IFileSystem, ICommandExecutor)` constructor, and is *not* a base/derived pair with `SystemdServiceInstaller` — both implement `IServiceInstaller` directly. This avoids leaking systemd specifics (e.g. `UnitDir`, journalctl tailing) into a shared base.

**Alternatives considered**

- *Common abstract base with a handful of shared helpers*: rejected. The only things the two implementations actually share are constructor parameter types, the option/command records, and the dry-run "print, don't act" rule — none of which justify an abstract class. The dry-run rule is enforced by tests on each implementation.

### 3. Launcher rendering and `schtasks` argument construction are pure functions

Two small static helpers in `WindowsScheduledTaskInstaller.cs` make the rendering rules testable without touching the filesystem or running `schtasks`:

- `RenderServerLauncher(ServerLauncherSpec spec)` → `string`
- `RenderRunnerLauncher(RunnerLauncherSpec spec)` → `string`
- `BuildCreateTaskArgs(TaskCreateSpec spec)` → `string[]` (the discrete argument list)
- `BuildRunArgs(string taskName)`, `BuildEndArgs(string taskName)`, `BuildDeleteArgs(string taskName)`, `BuildQueryArgs(string taskName)` → `string[]`
- `QuoteForCmdBody(string)` and `QuoteForSchtasksTr(string)` → `string` (two distinct helpers, per `cli-service-installer/spec.md:96-100`)

The `QuoteForCmdBody` helper follows cmd's `^` escape semantics (caret-escape `& | < > ^ "`, and wrap in double quotes when the result contains spaces). The `QuoteForSchtasksTr` helper follows `schtasks /TR` semantics: a single double-quoted string with `"` escaped as `\"` (cmd's native quoting for the `/TR` field is consistent with double quotes; this is the Hermes pattern).

The CLI never assembles a single shell string for `schtasks`; it always calls `_commandExecutor.ExecuteAsync("schtasks", args)` with the discrete list, and the fake executor in tests can assert against that exact list.

**Alternatives considered**

- *Build the full command line, hand it to `cmd /c`*: rejected by `cli-service-installer/spec.md:91-95` and the underlying "argument arrays, not shell string concatenation" requirement.
- *Use `ProcessStartInfo` `Arguments` (single string) for `schtasks`*: rejected for the same reason, and because `ProcessStartInfo.ArgumentList` is already available and used by `SystemCommandExecutor`.

### 4. Install flow: write launcher first, then attempt Scheduled Task, then fall back

`InstallServerAsync` (and the runner equivalent) on Windows follows this order:

1. Resolve the launcher spec (repo root, listen url, log path, etc.).
2. Render the launcher body and write `~/.mohist/service/mohist-server.cmd` (path resolution uses `%USERPROFILE%` via `Environment.GetFolderPath(SpecialFolder.UserProfile)`).
3. Build the `schtasks /Create /SC ONLOGON /RL LIMITED /TN Mohist_Server /TR "<launcher>" /F` argument list and call it.
4. If `schtasks /Create` exits non-zero (or returns "access denied"/"blocked"), write a Startup-folder `.cmd` shortcut at `%USERPROFILE%\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Mohist_Server.cmd` whose body is a single `call "<launcher>"` line. Record "fallback used" in the installer metadata side-channel.
5. Report success to the user in both cases, with a one-line note when fallback was used.

`--dry-run` short-circuits after step 1: it prints the would-be launcher path, a one-paragraph summary of the launcher body (no full body, to keep dry-run output diff-friendly), the exact `schtasks` argument list (as space-joined, shell-quoted preview plus a discrete-argument assertion message), and an explicit "would use Startup-fallback if Scheduled Task creation is blocked" note. It writes nothing and runs nothing.

The fallback shortcut is a thin file that re-invokes the generated launcher (`spec.md:124`), so `uninstall` only has one place to look for the real launcher and the Startup-folder entry stays small.

**Alternatives considered**

- *Always install both, Scheduled Task and Startup shortcut*: rejected by the issue ("if Scheduled Task creation is blocked or denied, falls back to a Startup-folder `.cmd` launcher") and by `cli-service-installer/spec.md:118-119` ("record that the Startup-fallback was used").
- *Detect Task Scheduler availability up front, branch in advance*: rejected — a single attempt with detection on failure is simpler and matches the Hermes pattern.

### 5. Lifecycle actions are backend-aware

`StartAsync` / `StopAsync` / `RestartAsync` consult a small backend-detection helper (`DetectBackend`): check whether `schtasks /Query /TN Mohist_Server` exits 0 and the task is present, otherwise check whether the Startup-folder shortcut exists, otherwise treat the install as "launcher-only" (no registration).

- `start` with Scheduled Task → `schtasks /Run /TN <name>`. With Startup-fallback only → `Process.Start` the launcher with `UseShellExecute = true` and `CreateNoWindow = true`, no shell-joined string. The process inherits stdout/stderr redirection through the launcher's `>>` append.
- `stop` always: `schtasks /End /TN <name>` when a task is present, plus `Process.Stop` (or `taskkill /F`) on the matching launcher-spawned PIDs when fallback or launcher-only is in use. The Scheduled Task is *not* deleted on stop.
- `restart` = `Stop` + `Start` with the same backend selection.
- `status` prints: install state (task / fallback / launcher file presence), live runtime state (process detection via `tasklist` filtered by image name and command-line, optional `/api/health` probe for the server). The output is plain text (not JSON) to match `SystemdServiceInstaller.StatusAsync`'s `systemctl status --no-pager` flow.
- `logs` reads the generated log file directly with `IFileSystem.OpenRead` plus a bounded `StreamReader.ReadLine` loop for `-n`, and a `FileSystemWatcher` + `CancellationToken` loop for `--follow`. This mirrors the journalctl path conceptually but stays inside our process.

`--dry-run` for these commands prints: which backend would be used, the exact `schtasks` / `tasklist` / `taskkill` argument list, and the would-be `Process.Start` file path; writes nothing.

**Alternatives considered**

- *Always use the launcher as the source of truth, ignore Scheduled Task state*: rejected — `spec.md:155-157` requires `schtasks /End` to be used when a task is present, and `spec.md:198-203` requires status to combine install registration with live state.

### 6. `UninstallServerAsync` / `UninstallRunnerAsync` are explicit, narrow, and never touch user data

The Windows uninstall performs, in order, *only* the actions from the install plan:

1. `schtasks /Delete /TN <name> /F` when a Scheduled Task exists.
2. Delete `%USERPROFILE%\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Mohist_<Server|Runner>.cmd` when present.
3. Delete `%USERPROFILE%\.mohist\service\mohist-<server|runner>.cmd` when present.
4. Delete the small installer-metadata file we wrote at install time (`%USERPROFILE%\.mohist\service\mohist-<server|runner>.install.json` — records "scheduled-task" vs "startup-fallback" so uninstall and status know which side-channels to clear).

It explicitly does *not* delete `~/.mohist/mohist.db`, `config.jsonc`, project worktrees, `~/.mohist/server/out.log`, `~/.mohist/runner/out.log`, or any other file under `~/.mohist/`. This is asserted by the new test specs.

`--dry-run` lists each path that would be deleted and prints the `schtasks /Delete` argument list, with no file or task changes.

**Alternatives considered**

- *Recursively delete `~/.mohist/service/`*: rejected — future installs may share the directory and we should not blow it away wholesale. Per-file deletes are scoped to what this install wrote.

### 7. Linux behavior is bit-for-bit unchanged

- `SystemdServiceInstaller` keeps its current constructor, public methods, unit-render output, and `systemctl --user` argument list. The only change is `class SystemdServiceInstaller` → `class SystemdServiceInstaller : IServiceInstaller`.
- `SourceCodeUpdater` updates its `SystemdServiceInstaller` field to `IServiceInstaller` and keeps every call site identical.
- `Program.cs` and `MohistCliCommands.RunAsync` register `IServiceInstaller` instead of `SystemdServiceInstaller` but the resolved instance is the same class on Linux.
- `InstallSpecs.cs` continues to instantiate `SystemdServiceInstaller` directly (allowed because the class is still public within the assembly) and assert on the same unit content.
- `SystemdInstallDetectorSpecs.cs` and `UpdateSpecs.cs` are unchanged.

### 8. Tests live alongside `InstallSpecs.cs` and use the existing `FakeFileSystem` + a small `FakeCommandExecutor` pattern

A new `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/WindowsInstallSpecs.cs` covers:

- `schtasks` argument-list construction for `/Create`, `/Run`, `/End`, `/Delete`, `/Query` — uses an inline `FakeCommandExecutor` identical to the one in `InstallSpecs.cs:132` and asserts on the recorded argument arrays.
- Launcher body rendering for server and runner: contains `cd /d <repo-root>`, `set "ASPNETCORE_URLS=..."` / `set "SERVER_URL=..."` + `set "RUNNER_ROOT=..."`, the documented `dotnet run` / `node ... cli.js` invocation, and the `>> "%USERPROFILE%\.mohist\server\out.log" 2>&1` redirection.
- Paths-with-spaces round-trip: a `C:\Users\Mohist User\repos\space repo` repo root is correctly quoted in the launcher and in `schtasks /TR`.
- `--dry-run` produces no writes and no commands: asserted by a `FakeCommandExecutor` whose `ExecutedCommands` list is empty after the run, and a `FakeFileSystem` whose `Files` snapshot is unchanged.
- `DetectBackend` selection: with a fake `schtasks /Query` exit code 0 the backend is "scheduled-task"; with exit code 1 and a Startup-folder file present the backend is "startup-fallback"; with neither, the backend is "launcher-only".
- `start` / `stop` / `restart` mapping: each calls the right `schtasks` verb when the task is present, and falls back to a `Process.Start` / `taskkill` recorded call when only the launcher file exists.
- `logs` tail: a fake log file is written, the installer reads the trailing N lines, and `--follow` attaches a watcher (verified by appending to the fake file and observing the emitted lines).
- `uninstall` cleanup: after uninstall, the launcher file, Startup-folder file, Scheduled Task (fake), and installer metadata are gone, but a separately-added `mohist.db` and `out.log` are untouched.
- Quote-helper separation: the rendered launcher body and the rendered `schtasks /TR` are produced by different helper functions and produce different outputs for the same path.

`--dry-run` for the new Windows path is asserted with the same pattern as the existing `InstallSpecs` dry-run tests.

## Risks / Trade-offs

- **[Risk] `schtasks` parsing of non-zero exit codes is ambiguous** — `schtasks /Create` returns 1 for many failure modes (access denied, task already exists, etc.), and "task exists" isn't a real failure. → *Mitigation*: pass `/F` on `/Create` to overwrite, treat any non-zero exit on `/Create` as "fall back to Startup", and parse `stderr` only for the human-readable message printed to the user. Tests assert the `/F` flag is present.

- **[Risk] `Process.Start` for fallback `start` is not guaranteed to outlive the parent terminal** — the issue explicitly says "be able to outlive the current terminal" but a child process inherits the parent's job object on Windows in some configurations. → *Mitigation*: use `ProcessStartInfo { UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }` and `Process.Start` the launcher, which gives the child its own console window. The Hermes pattern is the reference; if a more aggressive `CREATE_NEW_PROCESS_GROUP` / `DETACHED_PROCESS` flag combination is required to escape the job, it can be added in a follow-up without changing the public surface.

- **[Risk] Two separate quote helpers risk drifting from the cmd / schtasks quoting rules** — cmd and `schtasks /TR` have subtly different escape rules. → *Mitigation*: the two helpers are tiny pure functions with unit-tested tables of inputs; a regression in one helper is caught by the corresponding spec. The "distinct helpers" requirement is also asserted directly.

- **[Risk] The installer metadata file at `~/.mohist/service/mohist-<kind>.install.json` could conflict with a future cross-platform metadata design** → *Mitigation*: scoped to a single file per kind, deleted on uninstall, ignored by the systemd path. The path can be repurposed later if a unified metadata scheme lands.

- **[Risk] `SourceCodeUpdater` taking `IServiceInstaller` could break a Linux-only test that constructs `SystemdServiceInstaller` directly** → *Mitigation*: keep the existing constructor signature and public method surface on `SystemdServiceInstaller`; only the class declaration gains the interface. The constructor parameter type order is preserved.

- **[Trade-off] Adding `IServiceInstaller` adds a layer of indirection for Linux readers** — the systemd path now goes through an interface. The cost is negligible (one virtual dispatch per call) and the gain is the only way to satisfy the platform-neutral command surface requirement.

- **[Trade-off] `schtasks /Query` for backend detection is one extra process invocation per `start`/`status`** — `schtasks /Query` is typically <100 ms, and only `start`/`status`/`restart`/`uninstall` pay the cost. Acceptable for a CLI.

## Migration Plan

This change is additive and Linux-untouched at the runtime level, so deployment is just "merge and ship a new `mo` binary":

1. Land `IServiceInstaller`, refactor `SystemdServiceInstaller` to implement it, register the resolved instance in both `Program.cs` and `MohistCliCommands.RunAsync`. **At this point Linux behavior is unchanged and `InstallSpecs` / `UpdateSpecs` still pass.**
2. Update `MohistCliCommands.{Install,Server,Update}.cs` to depend on `IServiceInstaller`. Existing Linux paths still work.
3. Add `WindowsScheduledTaskInstaller`, its helpers, and the new spec file. Gate DI selection on `OperatingSystem.IsWindows()`.
4. Verify the new Windows specs pass on a Linux runner (they only depend on the pure rendering helpers and the fake command executor — they do not require a real Windows host).
5. Smoke test on a real Windows dev box: `mo install server` writes the launcher and registers the task; `mo server start` brings `/api/health` up; `mo server status` reports running; `mo server logs -n 50` tails the file; `mo server uninstall` removes the task and the launcher, leaves `mohist.db` and `out.log` alone.

**Rollback**

- Revert the merge. Because the Linux path is byte-identical and the Windows path is gated on `OperatingSystem.IsWindows()`, a partial rollout (e.g. shipping only the `IServiceInstaller` refactor) is safe on its own.
- The new spec file can be deleted with the Windows installer in the same revert; no other specs need to change.

## Open Questions

- Should `mo install server --listen-url` on Windows default to `http://localhost:3456` (matching the Linux default in `SystemdServiceInstaller.InstallServerAsync`) or `http://127.0.0.1:3456`? The Linux default is `127.0.0.1`. The issue body uses `http://localhost:3456` only as a `/api/health` check example. **Default to `http://127.0.0.1:3456` for parity with the Linux path; revisit if a Windows-specific reason emerges.**
- `schtasks /Create /F` overwrites a pre-existing task — should the installer print a "replaced existing task" line, or stay silent? The Hermes pattern is silent. **Stay silent unless the recorded backend changes; the spec only requires reporting "fallback used".**
- For the `mo server logs --follow` flow, should SIGINT/Ctrl+C stop both the `FileSystemWatcher` and any in-flight `ReadLine` cleanly, or is best-effort acceptable? The spec says "stop cleanly". **Use `FileSystemWatcher` with `CancellationToken` registration on `Console.CancelKeyPress`; tests assert that the loop exits within 200 ms after the token is cancelled.**
- Should `mo install server` on Windows default `--repo-root` to the discovered Mohist solution root (same as Linux), or require it to be passed? **Match Linux — discover from `Mohist.sln` walking up from `AppContext.BaseDirectory`; tests confirm the default discovery path is used when `--repo-root` is omitted.**
