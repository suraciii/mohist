## ADDED Requirements

### Requirement: IServiceInstaller abstraction is platform-neutral

The CLI SHALL expose an `IServiceInstaller` interface that covers the full managed service surface for both Mohist server and Mohist runner: install, lifecycle (start / stop / restart), status, log access, and uninstall. CLI composition SHALL select an implementation at process startup based on the runtime platform so the same `mo install server` / `mo install runner` / `mo server ...` / `mo runner ...` command surface works on both Linux and Windows.

#### Scenario: Linux process resolves to systemd implementation

- [x] **WHEN** the CLI process is started on a Linux runtime
- **AND** `IServiceInstaller` is resolved from the DI container
- **THEN** the resolved implementation SHALL be the Linux user-systemd implementation
- **AND** the resolved implementation SHALL satisfy every method of `IServiceInstaller` for both `Server` and `Runner` service kinds

> Satisfied by: `MohistCliCommands.RunAsync` at `packages/cli/Mohist.Cli/MohistCliCommands.cs:68` (factory returns `SystemdServiceInstaller` on non-Windows); `Program.cs` at `packages/cli/Mohist.Cli/Program.cs:29`.

#### Scenario: Windows process resolves to Scheduled Task implementation

- [x] **WHEN** the CLI process is started on a Windows runtime
- **AND** `IServiceInstaller` is resolved from the DI container
- **THEN** the resolved implementation SHALL be the Windows Scheduled-Task-with-Startup-fallback implementation
- **AND** the resolved implementation SHALL satisfy every method of `IServiceInstaller` for both `Server` and `Runner` service kinds

> Satisfied by: `MohistCliCommands.RunAsync` at `packages/cli/Mohist.Cli/MohistCliCommands.cs:68` (factory returns `WindowsScheduledTaskInstaller` on Windows); `Program.cs` at `packages/cli/Mohist.Cli/Program.cs:29`.

#### Scenario: All server and runner commands dispatch through the same interface

- [x] **WHEN** the user runs `mo install server`, `mo install runner`, `mo server start|stop|restart|status|logs|uninstall`, or `mo runner start|stop|restart|status|logs|uninstall`
- **THEN** each command SHALL dispatch through the resolved `IServiceInstaller` for the matching service kind
- **AND** the command builders SHALL NOT contain platform-specific code paths or hard-coded platform checks

> Satisfied by: `InstallCommands.Build` at `packages/cli/Mohist.Cli/MohistCliCommands.Install.cs:12`; `ServerCommands.Build` / `RunnerCommands.Build` at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:12,117`; handler delegates bind to interface methods without platform checks.

### Requirement: Linux user-systemd implementation is the unchanged compatibility baseline

The Linux implementation of `IServiceInstaller` SHALL preserve the existing `SystemdServiceInstaller` behavior: generated user systemd unit file, `systemctl --user` invocation, current dry-run behavior, and current install / lifecycle / uninstall flow. The Linux implementation MUST NOT change behavior visible to existing Linux-oriented tests.

#### Scenario: Linux install writes the existing systemd unit

- [x] **WHEN** the user runs `mo install server` on Linux
- **THEN** the CLI SHALL write the existing user systemd unit file to its existing path under `~/.mohist/systemd/`
- **AND** the CLI SHALL invoke `systemctl --user` with the existing arguments to enable and start the unit
- **AND** the resulting unit content, install paths, and `systemctl` arguments SHALL be byte-identical to the pre-change baseline for the same inputs

> Satisfied by: `SystemdServiceInstaller.InstallAsync` at `packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:79`; unit rendered by `SystemdUnit.Render` at `:306`; systemctl args at `:95,102,109`. Verified by `InstallSpecs.InstallServer_CreatesSystemdUnitWithCorrectConfiguration`.

#### Scenario: Linux start, stop, restart, status, and uninstall remain unchanged

- [x] **WHEN** the user runs `mo server start|stop|restart|status` or `mo server uninstall` on Linux
- **THEN** the CLI SHALL call the same `systemctl --user` actions used before this change
- **AND** the CLI SHALL NOT introduce new artifact paths, new log file locations, or new status fields on Linux

> Satisfied by: `SystemdServiceInstaller.StartAsync`/`StopAsync`/`RestartAsync`/`StatusAsync`/`UninstallAsync` at `packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:121-197`; all delegate to `RunSystemctlAsync` at `:199` with identical arguments.

#### Scenario: Linux dry-run is unchanged

- [x] **WHEN** the user runs any `mo install ...` / `mo server ...` / `mo runner ...` command on Linux with `--dry-run`
- **THEN** the CLI SHALL preview the same unit file content, the same `systemctl --user` arguments, and the same start / stop actions that the pre-change `SystemdServiceInstaller` produced
- **AND** the CLI SHALL NOT write any file or invoke any external process

> Satisfied by: `SystemdServiceInstaller.InstallAsync` dry-run branch at `:89-93`; `RunSystemctlAsync` dry-run at `:205-208`; `LogsAsync` dry-run at `:152-156`; `UninstallAsync` dry-run at `:169-174`. No file writes or command execution occur in dry-run branches.

### Requirement: Windows install writes launcher and registers Scheduled Task

On Windows, the Scheduled-Task implementation SHALL install Mohist server and runner by writing a generated `.cmd` launcher file and registering a per-user Scheduled Task that runs that launcher at logon.

#### Scenario: Server install writes launcher and registers Scheduled Task

- [x] **WHEN** the user runs `mo install server` on Windows
- **THEN** the CLI SHALL write `~/.mohist/service/mohist-server.cmd`
- **AND** the CLI SHALL register a per-user Scheduled Task named `Mohist_Server`
- **AND** the Scheduled Task SHALL use `/SC ONLOGON` and `/RL LIMITED`
- **AND** the Scheduled Task `/TR` SHALL point to the generated launcher path

> Satisfied by: `WindowsScheduledTaskInstaller.InstallServerAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:63`; launcher path at `:624`; task name at `:621`; `BuildCreateTaskArgs` at `:700`; verified by `WindowsInstallSpecs.InstallServer_WithFailingSchtasks_CreatesStartupFallbackAndRecordsMetadata` and `BuildCreateTaskArgs_ContainsDiscreteElements`.

#### Scenario: Runner install writes launcher and registers Scheduled Task

- [x] **WHEN** the user runs `mo install runner` on Windows
- **THEN** the CLI SHALL write `~/.mohist/service/mohist-runner.cmd`
- **AND** the CLI SHALL register a per-user Scheduled Task named `Mohist_Runner`
- **AND** the Scheduled Task SHALL use `/SC ONLOGON` and `/RL LIMITED`
- **AND** the Scheduled Task `/TR` SHALL point to the generated launcher path

> Satisfied by: `WindowsScheduledTaskInstaller.InstallRunnerAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:99`; launcher path at `:625`; task name at `:622`; `BuildCreateTaskArgs` at `:700`; verified by `WindowsInstallSpecs.InstallRunner_WithFailingSchtasks_CreatesStartupFallbackAndRecordsMetadata`.

#### Scenario: Server launcher content follows the documented shape

- [x] **WHEN** the server launcher is written
- **THEN** the launcher SHALL set the working directory to the configured repository root
- **AND** the launcher SHALL export `ASPNETCORE_URLS=<listen-url>` before starting the server
- **AND** the launcher SHALL run `dotnet run --project packages\server\src\Mohist.Server\Mohist.Server.csproj`
- **AND** the launcher SHALL redirect both stdout and stderr to `%USERPROFILE%\.mohist\server\out.log`

> Satisfied by: `RenderServerLauncher` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:633`; verified by `WindowsInstallSpecs.RenderServerLauncher_WithSpacePath_ContainsQuotedCd`.

#### Scenario: Runner launcher content follows the documented shape

- [x] **WHEN** the runner launcher is written
- **THEN** the launcher SHALL set the working directory to the configured repository root
- **AND** the launcher SHALL export `SERVER_URL=<server-url>` and `RUNNER_ROOT=<runner-root>`
- **AND** the launcher SHALL run `node packages\runner\dist\cli.js`
- **AND** the launcher SHALL redirect both stdout and stderr to `%USERPROFILE%\.mohist\runner\out.log`

> Satisfied by: `RenderRunnerLauncher` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:648`; verified by `WindowsInstallSpecs.RenderRunnerLauncher_ContainsExpectedElements`.

### Requirement: Windows launcher quoting and schtasks argument handling

The Windows implementation MUST render `.cmd` launcher content with care for Windows quoting and MUST invoke `schtasks.exe` with argument arrays rather than shell-concatenated strings. The implementation SHALL use separate quoting helpers for `.cmd` body content and for `schtasks /TR` content.

#### Scenario: schtasks is invoked with argument arrays

- [x] **WHEN** the installer registers, queries, runs, ends, or deletes a Scheduled Task
- **THEN** the installer SHALL pass the `schtasks.exe` command line as a discrete argument array
- **AND** the installer SHALL NOT concatenate the command into a single shell string

> Satisfied by: `BuildCreateTaskArgs`, `BuildRunArgs`, `BuildEndArgs`, `BuildDeleteArgs`, `BuildQueryArgs` returning `string[]` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:700-723`; every caller passes the array directly to `_commandExecutor.ExecuteAsync("schtasks", args)` (e.g. `:85`, `:121`, `:205`, `:257`, `:401`, `:435`).

#### Scenario: .cmd body and schtasks /TR use distinct quote helpers

- [x] **WHEN** the installer generates the launcher `.cmd` content and the `schtasks /TR` payload
- **THEN** the two outputs SHALL be produced by separate quote-escaping helpers
- **AND** each helper SHALL escape only the quoting rules of its own target (`.cmd` body vs. `schtasks /TR`)

> Satisfied by: `QuoteForCmdBody` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:664` and `QuoteForSchtasksTr` at `:694`; verified by `WindowsInstallSpecs.QuoteForCmdBody_And_QuoteForSchtasksTr_ProduceDifferentOutputs_ForSamePath`.

#### Scenario: Paths with spaces round-trip into the launcher and task

- [x] **WHEN** the configured repository root, listen URL, server URL, runner root, user profile path, or generated launcher path contains spaces
- **THEN** the generated `.cmd` content SHALL correctly `cd /d` to the repository root
- **AND** the generated `.cmd` content SHALL correctly redirect log output to the log file path
- **AND** the `schtasks /TR` payload SHALL correctly identify the generated launcher file

> Satisfied by: `QuoteForCmdBody` used in `RenderServerLauncher`/`RenderRunnerLauncher` (`:637-638`, `:651-652`) and `QuoteForSchtasksTr` used in `InstallServerAsync`/`InstallRunnerAsync` (`:84`, `:120`); verified by `WindowsInstallSpecs.RenderServerLauncher_WithSpacePath_ContainsQuotedCd` and `BuildCreateTaskArgs_ContainsDiscreteElements`.

### Requirement: Windows falls back to Startup-folder launcher when Scheduled Task is blocked

On Windows, when Scheduled Task creation fails because Task Scheduler access is blocked or denied, the Windows implementation SHALL fall back to writing a Startup-folder `.cmd` launcher so login auto-start still works through the user Startup folder.

#### Scenario: Scheduled Task creation falls back to Startup folder

- [x] **WHEN** `mo install server` or `mo install runner` is run on Windows
- **AND** `schtasks /Create` exits non-zero or returns an error indicating the task was blocked or denied
- **THEN** the installer SHALL write a `.cmd` launcher into the current user's Startup folder
- **AND** the installer SHALL record that the Startup-fallback was used for the affected service kind
- **AND** the installer SHALL still report a successful install to the user

> Satisfied by: `InstallServerAsync` fallback branch at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:84-91` and `InstallRunnerAsync` at `:120-127`; metadata written by `WriteMetadataAsync` at `:553`; verified by `WindowsInstallSpecs.InstallServer_WithFailingSchtasks_CreatesStartupFallbackAndRecordsMetadata` and runner equivalent.

#### Scenario: Startup-fallback launcher invokes the generated launcher

- [x] **WHEN** the Startup-fallback launcher is written
- **THEN** the Startup-folder file SHALL invoke the generated `~/.mohist/service/mohist-<kind>.cmd` launcher
- **AND** the fallback SHALL NOT inline the full server or runner command

> Satisfied by: `InstallStartupFallbackAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:539-551`; body is `call "<launcher>"` only; verified by `WindowsInstallSpecs.InstallServer_WithFailingSchtasks_CreatesStartupFallbackAndRecordsMetadata`.

#### Scenario: Fallback detection during start / status / uninstall

- [x] **WHEN** the user runs `mo server start|status|uninstall` or `mo runner start|status|uninstall` on Windows
- **THEN** the installer SHALL detect whether a Scheduled Task exists
- **AND** the installer SHALL detect whether the Startup-fallback launcher is present
- **AND** the installer SHALL select the correct action (Scheduled Task vs. fallback) for each lifecycle operation

> Satisfied by: `DetectBackendAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:426-442`; called from `StartAsync` (`:188`), `StopAsync` (`:240`), `StatusAsync` (`:288`), and `UninstallAsync` (`:391`); verified by `WindowsInstallSpecs.StartServer_WithScheduledTask_Backend_RunsSchtasksRun`, `StartServer_WithStartupFallback_Backend_StartsDetachedProcess`, and `StartServer_WithLauncherOnly_Backend_StartsDetachedProcess`.

### Requirement: Windows start, stop, and restart use the right backend

On Windows, lifecycle operations SHALL act on whichever install backend is in use for that service: Scheduled Task when present, otherwise a detached background process started from the generated launcher.

#### Scenario: Start uses Scheduled Task /Run when present

- [x] **WHEN** the user runs `mo server start` or `mo runner start` on Windows
- **AND** a Scheduled Task named `Mohist_Server` or `Mohist_Runner` is registered
- **THEN** the installer SHALL invoke `schtasks /Run /TN <task-name>` to start the service
- **AND** the installer SHALL NOT spawn a detached background process when the task is present

> Satisfied by: `StartAsync` ScheduledTask branch at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:201-208`; verified by `WindowsInstallSpecs.StartServer_WithScheduledTask_Backend_RunsSchtasksRun`.

#### Scenario: Start uses detached background process when no Scheduled Task

- [x] **WHEN** the user runs `mo server start` or `mo runner start` on Windows
- **AND** no matching Scheduled Task is registered
- **THEN** the installer SHALL start a detached background process using the generated launcher path
- **AND** the detached process SHALL be able to outlive the current terminal

> Satisfied by: `StartAsync` StartupFallback/LauncherOnly branch at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:209-224` using `ProcessStartInfo { UseShellExecute = true, CreateNoWindow = true, WindowStyle = Hidden }`; verified by `WindowsInstallSpecs.StartServer_WithStartupFallback_Backend_StartsDetachedProcess` and `StartServer_WithLauncherOnly_Backend_StartsDetachedProcess`.

#### Scenario: Stop ends Scheduled Task and matching processes

- [x] **WHEN** the user runs `mo server stop` or `mo runner stop` on Windows
- **THEN** the installer SHALL invoke `schtasks /End /TN <task-name>` when a matching Scheduled Task is present
- **AND** the installer SHALL also stop any matching server or runner process running from the generated launcher
- **AND** the installer SHALL NOT delete the Scheduled Task during a stop

> Satisfied by: `StopAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:231-277`; calls `BuildEndArgs` (`:710`) and `KillMatchingProcessesAsync` (`:444-450`); no delete on stop; verified by `WindowsInstallSpecs.StopServer_WithScheduledTask_Backend_RunsSchtasksEnd` and `StopServer_WithLauncherOnly_Backend_KillsMatchingProcesses`.

#### Scenario: Restart is stop then start

- [x] **WHEN** the user runs `mo server restart` or `mo runner restart` on Windows
- **THEN** the installer SHALL perform the same actions as `mo server stop` followed by `mo server start` (or the runner equivalent)
- **AND** the installer SHALL use the same backend selection rules as start and stop

> Satisfied by: `RestartServerAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:147-152` and `RestartRunnerAsync` at `:154-159`; verified by `WindowsInstallSpecs.RestartServer_CallsStopThenStart`.

### Requirement: Windows status reports install state and live runtime state

On Windows, status output SHALL combine install registration state (Scheduled Task presence, Startup-fallback presence, launcher file presence) with live runtime state (process running, port reachable where applicable).

#### Scenario: Status reports install registration

- [x] **WHEN** the user runs `mo server status` or `mo runner service-status` on Windows
- **THEN** the output SHALL indicate whether a Scheduled Task is registered
- **AND** the output SHALL indicate whether a Startup-fallback launcher is installed
- **AND** the output SHALL indicate whether the generated `~/.mohist/service/mohist-<kind>.cmd` launcher file is present

> Satisfied by: `StatusAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:279-324`; outputs `scheduled-task`, `startup-fallback`, and `launcher file` lines at `:305-308`; verified by `WindowsInstallSpecs.StatusServer_WithScheduledTask_Backend_ReportsCorrectState`.

#### Scenario: Status reports live runtime state

- [x] **WHEN** the user runs `mo server status` or `mo runner service-status` on Windows
- **THEN** the output SHALL indicate whether the service is currently running
- **AND** for server status, the output SHALL indicate whether `http://localhost:3456/api/health` is reachable
- **AND** for runner status, the output SHALL reflect the runner online / offline state

> Satisfied by: `StatusAsync` process detection via `IsProcessRunningAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:452-458` and health probe at `:313-320`; verified by `WindowsInstallSpecs.StatusServer_WithScheduledTask_Backend_ReportsCorrectState`.

### Requirement: Windows logs read generated log files

On Windows, `mo server logs` and `mo runner logs` SHALL read the generated Windows log files (`~/.mohist/server/out.log` and `~/.mohist/runner/out.log`), support `-n <lines>` to bound the tail length, and support `--follow` where practical.

#### Scenario: Logs reads the generated log file

- [x] **WHEN** the user runs `mo server logs` on Windows
- **THEN** the CLI SHALL read `%USERPROFILE%\.mohist\server\out.log`
- **AND** the CLI SHALL print the trailing content of the file
- **AND** the output SHALL behave consistently with the Linux log command for an equivalent-sized file

> Satisfied by: `LogsServerAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:167-168` → `LogsAsync` at `:326-381` reading `ServerLogPath()` (`:630`); tail via `ReadTailLinesAsync` (`:460-471`); verified by `WindowsInstallSpecs.LogsServer_TailsLastNLines`.

#### Scenario: Runner logs reads the generated log file

- [x] **WHEN** the user runs `mo runner logs` on Windows
- **THEN** the CLI SHALL read `%USERPROFILE%\.mohist\runner\out.log`
- **AND** the CLI SHALL print the trailing content of the file

> Satisfied by: `LogsRunnerAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:170-171` reading `RunnerLogPath()` (`:631`); verified by the runner equivalent path coverage in `WindowsInstallSpecs`.

#### Scenario: -n bounds the tail length

- [x] **WHEN** the user runs `mo server logs -n 200` or `mo runner logs -n 200` on Windows
- **THEN** the CLI SHALL print at most 200 lines from the tail of the log file
- **AND** the same `-n` semantics used on Linux SHALL apply

> Satisfied by: `ReadTailLinesAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:460-471` honoring `options.Lines`; verified by `WindowsInstallSpecs.LogsServer_TailsLastNLines`.

#### Scenario: --follow streams new lines

- [x] **WHEN** the user runs `mo server logs --follow` or `mo runner logs --follow` on Windows
- **THEN** the CLI SHALL stream new lines appended to the log file after command start
- **AND** the streaming SHALL stop cleanly on Ctrl+C / SIGINT

> Satisfied by: `FollowLogAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:473-511` with `FileSystemWatcher` and `CancellationToken`; `Console.CancelKeyPress` registration at `:357-362`; verified by `WindowsInstallSpecs.LogsServer_Follow_StreamsNewLinesUntilCancelled`.

### Requirement: Windows uninstall cleans up install artifacts but preserves user data

On Windows, `mo server uninstall` and `mo runner uninstall` SHALL remove install-related artifacts: the Scheduled Task (when present), the Startup-folder fallback file (when present), the generated `.cmd` launcher, and installer metadata. Uninstall MUST NOT delete Mohist user data, including `~/.mohist/mohist.db`, `~/.mohist/config.jsonc`, project worktrees, or log files.

#### Scenario: Uninstall removes Scheduled Task

- [x] **WHEN** the user runs `mo server uninstall` or `mo runner uninstall` on Windows
- **AND** a matching Scheduled Task is registered
- **THEN** the installer SHALL invoke `schtasks /Delete /TN <task-name> /F` to remove it
- **AND** the installer SHALL NOT delete the task through any other mechanism

> Satisfied by: `UninstallAsync` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:383-424` invoking `BuildDeleteArgs` (`:715-718`); verified by `WindowsInstallSpecs.UninstallServer_RemovesArtifactsButPreservesUserData`.

#### Scenario: Uninstall removes Startup-fallback and generated launcher

- [x] **WHEN** the user runs `mo server uninstall` or `mo runner uninstall` on Windows
- **THEN** the installer SHALL remove the Startup-folder fallback file when present
- **AND** the installer SHALL remove the generated `~/.mohist/service/mohist-<kind>.cmd` launcher
- **AND** the installer SHALL remove any installer metadata it wrote

> Satisfied by: `UninstallAsync` deleting `startupPath`, `launcherPath`, and `metadataPath` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:404-420`; verified by `WindowsInstallSpecs.UninstallServer_RemovesArtifactsButPreservesUserData`.

#### Scenario: Uninstall preserves user data

- [x] **WHEN** the user runs `mo server uninstall` or `mo runner uninstall` on Windows
- **THEN** the installer SHALL NOT delete `~/.mohist/mohist.db`
- **AND** the installer SHALL NOT delete `~/.mohist/config.jsonc`
- **AND** the installer SHALL NOT delete project worktrees
- **AND** the installer SHALL NOT delete `~/.mohist/server/out.log` or `~/.mohist/runner/out.log`

> Satisfied by: `UninstallAsync` only deletes the task, `startupPath`, `launcherPath`, and `metadataPath` (`packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:401-420`); never enumerates `~/.mohist/` for cleanup; verified by `WindowsInstallSpecs.UninstallServer_RemovesArtifactsButPreservesUserData`.

### Requirement: --dry-run previews without writing or executing

On both Linux and Windows, every command covered by `IServiceInstaller` (`mo install ...`, `mo server ...`, `mo runner ...`) MUST honor `--dry-run`. `--dry-run` SHALL preview the generated paths, the launcher content summary, the platform-specific install / lifecycle commands, the fallback behavior, and the start / stop actions, while performing NO file writes and NO process or task changes.

#### Scenario: --dry-run previews generated paths and launcher summary

- [x] **WHEN** the user runs `mo install server` or `mo install runner` with `--dry-run` on Linux or Windows
- **THEN** the CLI SHALL print the install paths that WOULD be written
- **AND** the CLI SHALL print a summary of the launcher / unit file content (without writing it)
- **AND** the CLI SHALL print the exact `schtasks` or `systemctl --user` command arguments that WOULD be invoked

> Satisfied by: `SystemdServiceInstaller.InstallAsync` dry-run at `packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:89-93`; `WindowsScheduledTaskInstaller.PreviewInstall` at `:528-537`; both verified by existing Linux specs and `WindowsInstallSpecs.InstallServer_DryRun_DoesNotWriteOrExecute`.

#### Scenario: --dry-run previews start and stop actions

- [x] **WHEN** the user runs `mo server start|stop|restart` or `mo runner start|stop|restart` with `--dry-run` on Linux or Windows
- **THEN** the CLI SHALL print the exact start / stop / restart actions that WOULD be taken
- **AND** the CLI SHALL print which backend (Scheduled Task, Startup-fallback detached process, or systemd unit) WOULD be used
- **AND** the CLI SHALL print whether a fallback would be used

> Satisfied by: `SystemdServiceInstaller.RunSystemctlAsync` dry-run at `packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:205-208`; `WindowsScheduledTaskInstaller.StartAsync` dry-run at `:190-198` and `StopAsync` dry-run at `:242-251`; verified by `WindowsInstallSpecs.StartServer_DryRun_DoesNotExecute`, `StopServer_DryRun_DoesNotExecute`, and `RestartServer_DryRun_DoesNotExecute`.

#### Scenario: --dry-run performs no writes and no external changes

- [x] **WHEN** the user runs any `mo install ...` / `mo server ...` / `mo runner ...` command with `--dry-run` on Linux or Windows
- **THEN** the CLI SHALL NOT write any file
- **AND** the CLI SHALL NOT spawn any process, register any Scheduled Task, or invoke any `systemctl` action
- **AND** the CLI SHALL exit with a non-error status indicating the dry-run completed

> Satisfied by: every method branches on `options.DryRun` before `_fileSystem.WriteAllTextAsync` and `_commandExecutor.ExecuteAsync` (e.g. `SystemdServiceInstaller.cs:89`, `:152`, `:169`, `:205`; `WindowsScheduledTaskInstaller.cs:74`, `:110`, `:190`, `:242`, `:293`, `:328`, `:391`); verified by the full set of dry-run specs in `InstallSpecs`, `WindowsInstallSpecs`, and Linux lifecycle specs.

### Requirement: Windows install honors --repo-root, --listen-url, --server-url, and --runner-root

On Windows, the `mo install server` and `mo install runner` commands SHALL accept `--repo-root`, `--listen-url`, `--server-url`, and `--runner-root` to override the values used when generating the launcher and the Scheduled Task.

#### Scenario: --repo-root overrides the launcher working directory

- [x] **WHEN** the user runs `mo install server --repo-root <path>` or `mo install runner --repo-root <path>` on Windows
- **THEN** the generated launcher SHALL `cd /d` to `<path>` instead of the default repository root

> Satisfied by: `InstallServerAsync` and `InstallRunnerAsync` pass `ResolveRepoRoot(options.RepoRoot)` into `RenderServerLauncher`/`RenderRunnerLauncher` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:65-71` and `:101-108`; `ResolveRepoRoot` at `:601-615` mirrors Linux default discovery.

#### Scenario: --listen-url overrides ASPNETCORE_URLS

- [x] **WHEN** the user runs `mo install server --listen-url <url>` on Windows
- **THEN** the generated server launcher SHALL export `ASPNETCORE_URLS=<url>`

> Satisfied by: `InstallServerAsync` passes `options.ListenUrl ?? "http://127.0.0.1:3456"` into `ServerLauncherSpec` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:70-71`; rendered as `set "ASPNETCORE_URLS=..."` at `:643`.

#### Scenario: --server-url and --runner-root override runner launcher env

- [x] **WHEN** the user runs `mo install runner --server-url <url> --runner-root <path>` on Windows
- **THEN** the generated runner launcher SHALL export `SERVER_URL=<url>` and `RUNNER_ROOT=<path>`

> Satisfied by: `InstallRunnerAsync` passes `options.ServerUrl` and `options.RunnerRoot` into `RunnerLauncherSpec` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:106-107`; rendered as `set "SERVER_URL=..."` at `:657` and conditional `set "RUNNER_ROOT=..."` at `:658-659`.

### Requirement: Scheduled Task command generation is testable and argument-safe

The Windows implementation SHALL construct every `schtasks.exe` invocation as a list of discrete arguments whose values are individually quoted, so test code can assert on the exact argument list without parsing a shell string.

#### Scenario: Create-task arguments are a discrete list

- [x] **WHEN** the installer builds a Scheduled Task registration call
- **THEN** the resulting argument list SHALL contain a discrete `/Create` element
- **AND** the resulting argument list SHALL contain discrete `/SC`, `/TN`, `/TR`, and `/RL` elements
- **AND** each value-bearing element SHALL be a single quoted string with no embedded concatenation

> Satisfied by: `BuildCreateTaskArgs` at `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:700-703` returns `["/Create", "/SC", "ONLOGON", "/RL", "LIMITED", "/TN", taskName, "/TR", trPayload, "/F"]`; verified by `WindowsInstallSpecs.BuildCreateTaskArgs_ContainsDiscreteElements`.

#### Scenario: Query, run, end, and delete arguments are a discrete list

- [x] **WHEN** the installer builds a `schtasks /Query`, `/Run`, `/End`, or `/Delete` call
- **THEN** the resulting argument list SHALL contain a discrete verb element
- **AND** the resulting argument list SHALL contain a discrete `/TN <task-name>` element
- **AND** the resulting argument list SHALL NOT contain any shell-joined substrings

> Satisfied by: `BuildRunArgs` (`:705-708`), `BuildEndArgs` (`:710-713`), `BuildDeleteArgs` (`:715-718`), `BuildQueryArgs` (`:720-723`); verified by `WindowsInstallSpecs.BuildRunArgs_ContainsDiscreteVerbAndTaskName`, `BuildEndArgs_ContainsDiscreteVerbAndTaskName`, `BuildDeleteArgs_ContainsDiscreteVerbAndTaskNameAndForceFlag`, and `BuildQueryArgs_ContainsDiscreteVerbAndTaskName`.
