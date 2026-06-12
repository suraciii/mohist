## Why

Windows developers cannot run Mohist server and runner as managed background processes. The `mo install server` / `mo install runner` and `mo server|runner start|stop|restart|status|logs|uninstall` commands today are wired to Linux user-systemd behavior, so on Windows users have to start both processes manually in terminal windows that die when the terminal closes — making it impossible to keep Mohist running unattended or auto-start it at login. The first Windows product step should follow the Hermes-shaped approach (per-user Scheduled Task with `/SC ONLOGON` plus a Startup-folder `.cmd` fallback) so that the same `mo ...` commands work on both platforms and Windows users get reliable background startup and login auto-start without forcing true Windows Service / SCM registration in this issue.

## What Changes

- Introduce a platform-neutral `IServiceInstaller` abstraction that covers installing server/runner, lifecycle operations (`start` / `stop` / `restart` / `status`), log access, and uninstall for both server and runner.
- Wrap the existing `SystemdServiceInstaller` behind `IServiceInstaller`; Linux behavior (generated unit file, `systemctl --user` commands, dry-run, current tests) stays unchanged as the compatibility baseline.
- Add a new `WindowsScheduledTaskInstaller` implementation that, on Windows, generates `~/.mohist/service/mohist-server.cmd` and `mohist-runner.cmd` launchers, registers per-user Scheduled Tasks named `Mohist_Server` / `Mohist_Runner` with `/SC ONLOGON /RL LIMITED`, and falls back to a Startup-folder `.cmd` shortcut when Scheduled Task creation is blocked or denied.
- CLI composition (`Mohist.Cli/Program.cs` and command builders) chooses the implementation at runtime via `OperatingSystem.IsWindows()` / `RuntimeInformation.IsOSPlatform(OSPlatform.Linux)` so the same `mo install server` / `mo server ...` / `mo runner ...` command surface behaves correctly on both platforms.
- Windows command execution must call `schtasks.exe` with argument arrays (not shell-concatenated strings) and must render `.cmd` launcher content with careful quoting, using separate quote helpers for `.cmd` body and `schtasks /TR` content (Hermes pattern).
- `mo server logs` and `mo runner logs` on Windows read `~/.mohist/server/out.log` / `~/.mohist/runner/out.log` and support `-n` and `--follow`.
- `mo server uninstall` and `mo runner uninstall` on Windows remove the Scheduled Task (when present), the Startup-folder fallback file, the generated `.cmd` launcher, and the installer metadata — but never delete `~/.mohist/mohist.db`, `config.jsonc`, project worktrees, or log files.
- `--dry-run` is honored on both platforms: it must preview generated paths, launcher content summary, `schtasks` commands, fallback behavior, and start/stop actions without writing files or running any process / task change.
- Add Windows-side unit tests covering Scheduled Task command generation, fallback launcher generation, dry-run behavior, lifecycle command mapping, log tailing, and uninstall cleanup. Linux tests continue to pass unchanged.

## Capabilities

### New Capabilities

- `cli-service-installer`: cross-platform service-management surface for Mohist server and runner — covers `mo install server` / `mo install runner`, `mo server|runner start|stop|restart|status|logs|uninstall`, the `IServiceInstaller` abstraction, the Linux user-systemd implementation (baseline), the Windows Scheduled-Task-with-Startup-fallback implementation, dry-run semantics, and the artifacts each platform writes under `~/.mohist/`. Each new platform-specific requirement (Windows install, fallback, lifecycle mapping, logs, uninstall) is expressed under this capability.

### Modified Capabilities

- `server-daemon`: extend the existing server-lifecycle requirements to clarify that `mo install server`, `mo server restart`, `mo server logs`, and `mo server uninstall` are part of the same managed service surface and that their platform behavior is selected by `IServiceInstaller` (no Linux behavior change — the existing systemd unit and `systemctl --user` commands remain the source of truth for Linux).
- `cli-interface`: extend the existing CLI command-shape requirement to cover the unified `mo install server|runner` / `mo server|runner start|stop|restart|status|logs|uninstall` surface on both Linux and Windows, including the requirement that `--dry-run` performs no writes or external commands on either platform.

## Impact

- `packages/cli/Mohist.Cli/SystemdServiceInstaller.cs` — implement / extend to satisfy `IServiceInstaller` while keeping the existing unit file generation, `systemctl --user` invocation, and dry-run behavior unchanged.
- `packages/cli/Mohist.Cli/IServiceInstaller.cs` (new) — interface for install / lifecycle / status / logs / uninstall covering both server and runner.
- `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs` (new) — Windows implementation: `.cmd` launcher rendering, `schtasks.exe` argument-array invocation, Scheduled-Task-or-Startup-fallback install flow, lifecycle command mapping, log tailing, uninstall cleanup.
- `packages/cli/Mohist.Cli/Program.cs` and `MohistCliCommands.{Install,Server,Runner,Update}.cs` — register the platform-selected `IServiceInstaller` in DI and switch all `SystemdServiceInstaller` references in command builders to the new interface.
- `packages/cli/Mohist.Cli/MohistCliCommands.cs` — replace the `AddSingleton<SystemdServiceInstaller>()` registration with `AddSingleton<IServiceInstaller>(...)` selecting implementation by platform.
- Generated artifacts:
  - Linux (unchanged): systemd user unit files under `~/.mohist/systemd/`.
  - Windows (new): `~/.mohist/service/mohist-server.cmd`, `~/.mohist/service/mohist-runner.cmd`, `~/.mohist/server/out.log`, `~/.mohist/runner/out.log`, per-user Scheduled Tasks `Mohist_Server` / `Mohist_Runner`, optional Startup-folder `.cmd` shortcuts.
- Tests: keep `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/InstallSpecs.cs`, `UpdateSpecs.cs`, and Skills specs that exercise `SystemdServiceInstaller` working as-is; add new Windows-side tests under `packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/WindowsInstallSpecs.cs` (and supporting helpers) covering Scheduled Task command generation, fallback launcher generation, dry-run, lifecycle command mapping, log tailing, and uninstall cleanup.
- No changes to the server runtime (`Mohist.Server` / ASP.NET Core host) and no changes to the runner runtime — the launchers only wrap their existing `dotnet run` / `node ... cli.js` entry points.
- Non-Goals explicitly excluded: true Windows Service / SCM registration, WinSW, ASP.NET Core `UseWindowsService()`, auto-installing `dotnet` / Node / opencode, GUI for Windows service management, and deleting user data (`mohist.db`, `config.jsonc`, worktrees, logs) on uninstall.
