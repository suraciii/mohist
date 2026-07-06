## Why

`design/cli.md`「唯一入口」节定明：同一能力只有一条命令路径，等价命令并存即契约违背。装机与升级是"系统级动词"，需要选定单一归属——`docs/cli-reference.md`「安装与升级」段已裁定归属为**动词根集中**（`mo install <组件>` / `mo update [组件]`）。但当前 `mo server` / `mo runner` 资源组下仍挂着与动词根完全等价的 `install` / `update` 子命令，打同一组 `IServiceInstaller` / `SourceCodeUpdater` 方法，没有任何一方独有的能力或参数。这让使用者在 `mo install server` 与 `mo server install` 之间猜测该用哪个，违背"命令面是稳定契约"与"可发现性"。本 issue 落地 spec 已定的归属选择：保留动词根、删除资源下的装机/升级冗余入口——这是命令面收敛批次（epic #41）里收敛装机动词的一步。

## What Changes

收敛是命令**路径**删除，不改变动词根入口的实际行为（installer / updater 方法、flags、输出语义不变）：

- **`mo server install` 删除**。原命令 `MohistCliCommands.Server.cs:58`（`ServerCommands.BuildInstall`）与 `mo install server`（`MohistCliCommands.Install.cs:20`）共用同一个 `IServiceInstaller.InstallServerAsync`，是重复入口。**BREAKING**。
- **`mo server update` 删除**。原命令 `MohistCliCommands.Server.cs:80`（`ServerCommands.BuildUpdate`）与 `mo update server`（`MohistCliCommands.Update.cs:62` `BuildServerUpdate`）共用同一个 `SourceCodeUpdater.UpdateServerAsync`，是重复入口。**BREAKING**。
- **`mo runner install` 删除**。原命令 `MohistCliCommands.Server.cs:295`（`RunnerCommands.BuildInstall`）与 `mo install runner`（`MohistCliCommands.Install.cs:42`）共用同一个 `IServiceInstaller.InstallRunnerAsync`，是重复入口。**BREAKING**。
- **`mo runner update`**：issue 表列其为待删路径，但**当前代码中并不存在**——`RunnerCommands.Build`（`MohistCliCommands.Server.cs:137`）只注册 `install`，从未注册 `update`；执行器升级只有动词根 `mo update runner`（`MohistCliCommands.Update.cs:78` `BuildRunnerUpdate`）一条入口。故此项无代码可删，验收点「`mo runner update` 删除」天然满足。
- **动词根入口行为不变**：`mo install server` / `mo install runner`（仍调 `IServiceInstaller.Install*Async`）、`mo update` / `mo update cli` / `mo update server` / `mo update runner`（仍调 `SourceCodeUpdater.Update*Async`）的 flags 与语义不动。
- **资源组非装机子命令不动**：`mo server` 的 `start` / `stop` / `restart` / `status` / `logs` / `health` / `uninstall` / `info`，`mo runner` 的 `start` / `stop` / `restart` / `service-status` / `logs` / `uninstall` / `list` / `show` / `status` 全部保留，本 issue 只删装机/升级两个动词。
- **`docs/cli-reference.md` 实装差距表更新**：双入口合并那行（行 340）移除；「Runner」段（行 255 的 `mo runner install`）、「Server」段（行 271–272 的 `mo server install/update`）、命令路径迁移提示（行 278）同步删除——正文「安装与升级」段已是目标，差距收敛后无需再标迁移。
- **AGENTS.md / `docs/self-host.md` / `docs/hermes-notifications.md` 无需改**：它们用的是动词根形态（`mo install server/runner`、`mo update server/runner`），与合并方向一致。

## Capabilities

每项收敛的目标行为由对应 capability spec 描述。按动词（装机 / 升级）切分，因二者 handler 不同（`IServiceInstaller` vs `SourceCodeUpdater`）、验收形状不同。

- `install-single-entry`: 动词根 `mo install server` / `mo install runner` 是装机的唯一入口，行为不变（同一 `IServiceInstaller.Install*Async`、同一 flags 集）；`mo server install` / `mo runner install` 从对应资源组移除，调用时解析失败、非零退出、不产生任何装机副作用。
- `update-single-entry`: 动词根 `mo update` / `mo update cli` / `mo update server` / `mo update runner` 是升级的唯一入口，行为不变（同一 `SourceCodeUpdater.Update*Async`、同一 flags 集）；`mo server update` 从 server 资源组移除，调用时解析失败、非零退出、不产生任何升级副作用。`mo runner update` 在代码中本就不存在，spec 确认其继续不存在（执行器升级只走 `mo update runner`）。

两项 spec 同时承载回归守卫：`mo server` / `mo runner` 的非装机子命令（start/stop/restart/status/logs/health/uninstall/info 及 runner 的 list/show/service-status）在新形状下仍正常解析与执行。

## Impact

- **代码**：`packages/cli/Mohist.Cli/MohistCliCommands.Server.cs`
  - `ServerCommands.Build`（行 15–24）：删除 `server.Subcommands.Add(BuildInstall(installer))`（行 16）与 `server.Subcommands.Add(BuildUpdate(updater))`（行 17）；删除 `BuildInstall`（行 58–78）与 `BuildUpdate`（行 80–94）两个私有方法。若删除后 `installer` / `updater` 局部变量在 `ServerCommands` 内不再被使用，一并清理。
  - `RunnerCommands.Build`（行 143）：删除 `runner.Subcommands.Add(BuildInstall(installer))`；删除 `BuildInstall`（行 295–318）。
  - `InstallCommands`（`MohistCliCommands.Install.cs`）与 `UpdateCommands`（`MohistCliCommands.Update.cs`）**不改**——它们是保留的动词根入口。
- **测试**：`packages/cli/tests/Mohist.Cli.Tests/`
  - `CliRunnerCommandSpecs.cs` / 现有 server 命令 spec：迁移对 `mo server install` / `mo server update` / `mo runner install` 的覆盖——改为断言这三条路径解析失败、非零退出、不触发 installer/updater（参考 `CliRootCommandShapeSpecs.cs` 的 `Legacy*` 模式）。
  - 新增/保留对动词根 `mo install server/runner` / `mo update server/runner` 行为不变的断言。
  - 回归：`mo server start/stop/restart/status/logs/health/uninstall/info` 与 `mo runner start/stop/restart/service-status/logs/uninstall/list/show/status` 仍可解析。
  - 符合 `design/testing.md`（无真实外部依赖、无墙钟；installer/updater 走 fake）。
- **文档**：`docs/cli-reference.md` —— 差距表（行 340）移除双入口合并行；「Runner」段（行 255）删 `mo runner install` 行；「Server」段（行 271–272）删 `mo server install` / `mo server update` 两行；命令路径迁移提示（行 278）删除。
- **依赖 / API**：无 server 端改动，无 schema 迁移——纯 CLI 命令面收敛，`IServiceInstaller` 与 `SourceCodeUpdater` 的方法签名、调用方均不变。
- **破坏面**：3 条命令路径删除（`mo server install` / `mo server update` / `mo runner install`），现有脚本/CI/远程 SSH 会话中用这些路径的会破。release/changelog 提示迁移到动词根 `mo install <组件>` / `mo update <组件>`。
