## MODIFIED Requirements

### Requirement: Server 可以作为后台进程启动 [UPDATED]

Server SHALL 能够作为独立的后台进程运行，持续监听 HTTP 请求。Server 启动时 SHALL 加载 `~/.mohist/config.jsonc` 配置文件。The `mo install server` and `mo server start` commands SHALL be part of the managed service surface driven by `IServiceInstaller`; on Linux the existing user-systemd unit and `systemctl --user` commands remain the source of truth, and on Windows the Scheduled-Task-with-Startup-fallback installer under `cli-service-installer` is the source of truth.

#### Scenario: 启动 server
- [x] **WHEN** 用户执行 `mo server start`
- **THEN** server 在 localhost:3456 监听 HTTP 请求
- **AND** server 初始化 Agent Runtime
- **AND** server 加载 `~/.mohist/config.jsonc` 配置（如果存在）
- **AND** server 在后台运行（daemon 模式）

> Satisfied by: `ServerCommands.BuildSystemd` binds `installer.StartServerAsync` at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:18`; on Linux `SystemdServiceInstaller.StartAsync` calls `systemctl --user start` (`packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:121`); on Windows `WindowsScheduledTaskInstaller.StartAsync` calls `schtasks /Run` or starts a detached launcher process (`packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:179`). The server runtime itself is unchanged.

#### Scenario: Server 重启后恢复状态
- [x] **WHEN** server 重启
- **THEN** server 从 SQLite 加载项目列表
- **AND** server 重新加载 `~/.mohist/config.jsonc` 配置
- **AND** Agent Runtime 就绪后等待新的 issue 启动请求

> Satisfied by: unchanged server runtime behavior; `SourceCodeUpdater.UpdateServerAsync` calls `_systemd.RestartServerAsync` at `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:308` and waits for readiness at `:411`; on Windows the same `IServiceInstaller.RestartServerAsync` path is used.

#### Scenario: 配置文件不存在
- [x] **WHEN** `~/.mohist/config.jsonc` 不存在
- **THEN** server 使用默认配置（无 model 指定，依赖环境变量）
- **AND** server 正常启动

#### Scenario: 配置文件格式错误
- [x] **WHEN** `~/.mohist/config.jsonc` 存在但格式错误
- **THEN** server 打印明确的解析错误
- **AND** server 以非零 exit code 退出

#### Scenario: Install and start are part of the IServiceInstaller surface

- [x] **WHEN** the user runs `mo install server` followed by `mo server start` on any supported platform
- **THEN** both commands SHALL dispatch through the resolved `IServiceInstaller` implementation for the runtime platform
- **AND** the server SHALL come up listening on the configured listen URL
- **AND** on Linux the install SHALL use the user-systemd unit and on Windows the install SHALL use the Windows installer defined in `cli-service-installer`

> Satisfied by: `ServerCommands.BuildInstall` and `BuildSystemd` resolve `IServiceInstaller` at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:12` and dispatch to `InstallServerAsync`/`StartServerAsync`; DI factory at `MohistCliCommands.RunAsync:68` and `Program.cs:29` selects the platform implementation; `WindowsScheduledTaskInstaller` launcher sets `ASPNETCORE_URLS` (`packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:643`).

### Requirement: Server 可以被显式停止 [UPDATED]

Server SHALL 支持用户显式停止。`mo server stop` SHALL dispatch through the platform's `IServiceInstaller` implementation, which on Linux uses the existing `systemctl --user` action and on Windows ends the Scheduled Task and stops matching processes (see `cli-service-installer`).

#### Scenario: 停止 server
- [x] **WHEN** 用户执行 `mo server stop`
- **THEN** Agent Runtime 优雅停止（等待当前 agent 完成，最多 30 秒）
- **AND** server 进程终止

> Satisfied by: on Linux `SystemdServiceInstaller.StopAsync` calls `systemctl --user stop` (`packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:127`) which uses the generated unit's `TimeoutStopSec=30`; on Windows `WindowsScheduledTaskInstaller.StopAsync` calls `schtasks /End` and `taskkill /F` for matching processes (`packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:231-277`).

#### Scenario: 查看 server 状态
- [x] **WHEN** 用户执行 `mo server status`
- **THEN** 显示 server 运行状态（运行中/未运行）
- **AND** 如果运行中，显示 PID、端口、运行时间、活跃 Issue 数

> Satisfied by: on Linux `SystemdServiceInstaller.StatusAsync` calls `systemctl --user status` (`packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:139`); on Windows `WindowsScheduledTaskInstaller.StatusAsync` prints `running` and `health` reachability (`packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:279-324`).

#### Scenario: Stop routes through IServiceInstaller

- [x] **WHEN** the user runs `mo server stop` on any supported platform
- **THEN** the CLI SHALL dispatch through the resolved `IServiceInstaller` implementation
- **AND** the CLI SHALL NOT use a hard-coded platform-specific stop path
- **AND** on Linux the existing systemd-based stop behavior SHALL be preserved unchanged

> Satisfied by: `ServerCommands.BuildSystemd("stop", ...)` at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:19` binds the interface method; no platform check in the command builder; Linux behavior unchanged in `SystemdServiceInstaller.StopAsync` (`packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:127`).

#### Scenario: Restart and uninstall are part of the managed service surface

- [x] **WHEN** the user runs `mo server restart`, `mo server logs`, or `mo server uninstall` on any supported platform
- **THEN** each command SHALL dispatch through the resolved `IServiceInstaller` implementation
- **AND** each command SHALL use the same platform-specific backend as `mo install server` and `mo server start` (systemd on Linux, Scheduled-Task or Startup-fallback on Windows)

> Satisfied by: `ServerCommands.Build` binds `RestartServerAsync`, `LogsServerAsync`, `UninstallServerAsync` at `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:20-23`; all resolved from `IServiceInstaller` registered by the platform factory at `MohistCliCommands.RunAsync:68` and `Program.cs:29`.
