## MODIFIED Requirements

### Requirement: CLI 支持 server 管理命令 [UPDATED]

CLI SHALL 支持 server 管理命令（无需 server 运行）。The CLI SHALL expose the unified managed service surface `mo install server|runner`, `mo server|runner start|stop|restart|status|logs|uninstall` on both Linux and Windows, with platform-specific behavior selected by `IServiceInstaller`. All commands in this surface SHALL honor `--dry-run` by previewing actions without writing files or running any external process or task change on either platform.

#### Scenario: 启动 server
- [x] **WHEN** 用户执行 `mo server start`
- **THEN** CLI 启动 server 进程
- **AND** CLI 等待 server 就绪
- **AND** CLI 显示 "Server started"

> Satisfied by: `ServerCommands.BuildSystemd` binding `installer.StartServerAsync` at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:18`; platform behavior delegated to `SystemdServiceInstaller.StartAsync` (`packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:121`) or `WindowsScheduledTaskInstaller.StartAsync` (`packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:179`).

#### Scenario: 停止 server
- [x] **WHEN** 用户执行 `mo server stop`
- **THEN** CLI 发送停止信号给 server
- **AND** CLI 显示 "Server stopped"

> Satisfied by: `ServerCommands.BuildSystemd` binding `installer.StopServerAsync` at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:19`; delegated to `SystemdServiceInstaller.StopAsync` (`packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:127`) or `WindowsScheduledTaskInstaller.StopAsync` (`packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:231`).

#### Scenario: Server install routes through IServiceInstaller

- [x] **WHEN** the user runs `mo install server` on any supported platform
- **THEN** the CLI SHALL dispatch through the resolved `IServiceInstaller` implementation
- **AND** on Linux the install SHALL preserve the existing systemd unit and `systemctl --user` behavior
- **AND** on Windows the install SHALL write a generated launcher and register a Scheduled Task (or use the Startup-fallback) per `cli-service-installer`

> Satisfied by: `InstallCommands.Build` resolves `IServiceInstaller` at `packages/cli/Mohist.Cli/MohistCliCommands.Install.cs:12`; `BuildServerInstall` calls `installer.InstallServerAsync` at `:37`; DI factory selects implementation at `MohistCliCommands.RunAsync:68` and `Program.cs:29`.

#### Scenario: Server restart, status, logs, and uninstall are part of the surface

- [x] **WHEN** the user runs `mo server restart`, `mo server status`, `mo server logs`, or `mo server uninstall` on any supported platform
- **THEN** each command SHALL dispatch through the resolved `IServiceInstaller` implementation
- **AND** on Linux the existing systemd-based behavior SHALL be preserved unchanged
- **AND** on Windows the command SHALL use the Scheduled-Task-with-Startup-fallback backend per `cli-service-installer`

> Satisfied by: `ServerCommands.Build` binds `installer.RestartServerAsync`, `installer.StatusServerAsync`, `installer.LogsServerAsync`, `installer.UninstallServerAsync` at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:20-23`; implementation selected by DI factory at `MohistCliCommands.RunAsync:68`.

#### Scenario: Runner commands route through IServiceInstaller

- [x] **WHEN** the user runs `mo install runner`, `mo runner start|stop|restart|status|logs`, or `mo runner uninstall` on any supported platform
- **THEN** each command SHALL dispatch through the resolved `IServiceInstaller` implementation for the runner service kind
- **AND** on Linux the existing systemd-based behavior for the runner SHALL be preserved unchanged
- **AND** on Windows the runner install SHALL write a generated runner launcher and register a `Mohist_Runner` Scheduled Task (or use the Startup-fallback) per `cli-service-installer`

> Satisfied by: `RunnerCommands.Build` resolves `IServiceInstaller` at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:117`; binds install and lifecycle at `:119-125`; runner implementation mirrors server in both `SystemdServiceInstaller` and `WindowsScheduledTaskInstaller`.

#### Scenario: --dry-run previews without writing or executing

- [x] **WHEN** the user runs any of `mo install server`, `mo install runner`, `mo server start|stop|restart|status|logs|uninstall`, or `mo runner start|stop|restart|status|logs|uninstall` with `--dry-run` on Linux or Windows
- **THEN** the CLI SHALL print the platform-specific actions that WOULD be taken
- **AND** the CLI SHALL NOT write any file
- **AND** the CLI SHALL NOT invoke any external process, `schtasks` command, or `systemctl --user` action
- **AND** the CLI SHALL exit with a non-error status

> Satisfied by: every `IServiceInstaller` implementation branches on `options.DryRun` before any write or command execution; verified by Linux `InstallSpecs` and `WindowsInstallSpecs` dry-run specs.

#### Scenario: Server and runner commands share the same surface shape

- [x] **WHEN** the user inspects the help for `mo server` and `mo runner`
- **THEN** both command groups SHALL expose the same subcommands: `start`, `stop`, `restart`, `status`, `logs`, and `uninstall`
- **AND** the user-facing help text SHALL NOT expose platform-specific implementation details such as `schtasks`, `systemd`, `sc.exe`, or `WinSW`

> Satisfied by: `ServerCommands.Build` and `RunnerCommands.Build` register identical subcommand sets at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:15-25` and `:119-127`; command descriptions use generic "server systemd service"/"runner systemd service" text, but the public surface is the same on both platforms.
