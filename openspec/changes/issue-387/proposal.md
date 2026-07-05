## Why

`design/cli.md` 「命令形状」节定明：根命令层只接受资源或资源组，不接受裸动词——唯一受控例外是跨资源全局只读诊断（`mo info`）。当前根上挂着 5 个裸动词（`status` / `logs` / `use` / `notify` / `system info`），它们各自其实属于某个资源（project / system / project / notification / server），却因为历史原因留在根上。这让使用者无法仅凭 `mo --help` 推断命令结构、也无法从命令名预测归属，违背"命令面是稳定契约"与"可发现性"两条设计目标。本 issue 把它们逐个归位，让根命令层契约真正成立——这是命令面收敛批次（epic #41）里净化根命令层的一步。

## What Changes

五项归位都是命令**路径**迁移，不改变各命令的实际行为（端点、输出、flag 语义不变）：

- **`mo status` → `mo project status`**。原命令 `MohistCliCommands.cs:219` 打 `/api/status?all=true`，是 project 聚合状态（all=true 拉全部项目），归 project。**BREAKING**——破坏面最大（高频命令），按下方别名策略过渡。
- **`mo logs` → `mo system logs`**。原命令 `MohistCliCommands.cs:226` 打 `/api/logs/tail`，是应用日志，归 system 诊断组。与 `mo server logs`（运维日志，systemd/计划任务层）语义不同，分置 `system` / `server` 两组。**BREAKING**。
- **`mo use <project>` 删除**。原命令 `MohistCliCommands.cs:233` 与 `mo project use`（`Project.cs:79`）共用同一个 `UseProjectAsync`，是重复入口——删冗余而非改行为。`mo project use` 是唯一入口。**BREAKING**。
- **`mo notify setup` → `mo notification setup`**。原命令组名 `notify`（`Notify.cs:63`）是动词；资源化成名词 `notification` 后归位到根命令层（root 上保留一个 `notification` 资源组，下面挂 `setup`）。`setup` 子命令的行为、flags（`--health-base` / `--webhook-url` / `--platform` / `--deliver-chat-id`）不变。**BREAKING**。
- **`mo system info` → `mo server info`**。原命令 `System.cs:19` 与 `mo info` 同名易混——`mo info` 是 CLI 本地环境（受控例外，不动），`mo system info` 是服务端系统诊断，改名 `mo server info` 消歧：一本地、一服务端。命令行为不变（仍调 `PrintSystemInfoAsync`）。**BREAKING**。
- **`mo info` 不动**。它是跨资源只读诊断的受控例外（CLI 本地环境与安装来源），不归任一单一资源，保留在根上。
- **旧路径别名策略统一**。本 issue 取向是直接迁移（spec 迁移表已标注旧路径不再可用），但实现时按破坏面评估——若保留过渡别名，五项要么全保留、要么全不保留，决策记录在 issue 评论，避免一项一策略。
- **`docs/cli-reference.md` 实装差距表更新**：5 行（`mo status` / `mo logs` / `mo use` / `mo notify setup` / `mo system info`）从差距表移除，对应命令组段落同步更新。

## Capabilities

每项归位的目标行为由对应 capability spec 描述。`mo info` 不在本 issue 范围（受控例外保留），不开 spec。

- `project-status`: `mo project status` 复刻原 `mo status` 行为——打 `/api/status?all=true`，输出与原命令一致；属 project 资源组（与 `project list/get/use` 同层）。
- `system-logs`: `mo system logs` 复刻原 `mo logs` 行为——打 `/api/logs/tail`，输出与原命令一致；属 system 资源组。与 `mo server logs`（运维日志）的内容差异由命令组说明承载。
- `project-use-single-entry`: `mo use` 删除后，`mo project use <名或id>` 是设置当前项目的唯一入口（行为不变，仍调 `UseProjectAsync`）。
- `notification-setup`: `mo notification setup` 复刻原 `mo notify setup` 行为——probe Hermes 健康、生成签名密钥、写 Mohist 出站 Hermes 配置、打印 hermes 订阅命令；flags（`--health-base` / `--webhook-url` / `--platform` / `--deliver-chat-id`）不变。`notification` 升格为根资源组。
- `server-info`: `mo server info` 复刻原 `mo system info` 行为——服务端系统诊断（identity / source / install / update / services / paths），仍调 `PrintSystemInfoAsync`，支持 `-o table|json|yaml`；归 server 资源组，与 `mo info`（CLI 本地）消歧。
- `root-command-shape`: 根命令层契约的收敛——根直接子命令只剩资源/资源组与受控例外 `mo info`，五个裸动词全部从根移除；旧路径别名策略（统一保留或统一删除）在这项 capability 里裁定。

## Impact

- **代码**：`packages/cli/Mohist.Cli/`
  - `MohistCliCommands.cs`：删除根级 `BuildStatusCommand` / `BuildLogsCommand` / `BuildUseCommand`（行 219–244）及对应 `root.Subcommands.Add(...)` 调用（行 14–15、24）。
  - `MohistCliCommands.Project.cs`：新增 `BuildStatus`，挂到 `project` 组。
  - `MohistCliCommands.System.cs`：新增 `BuildLogs`，挂到 `system` 组；移除 `BuildInfo`（迁去 server）。
  - `MohistCliCommands.Server.cs`：新增服务端系统诊断子命令（接收原 `BuildInfo` 的 handler），挂到 `server` 组。
  - `MohistCliCommands.Notify.cs`：`notify` 命令组更名为 `notification`（或新建 `NotificationCommands`）；`setup` 子命令不变。
  - 根 `Build`（`MohistCliCommands.cs:14–34`）调用顺序与列表同步更新。
- **测试**：`packages/cli/tests/` —— 现有覆盖 `mo status` / `mo logs` / `mo use` / `mo notify setup` / `mo system info` 的 CLI 测试迁移到新路径；新增对每个旧路径的解析失败（或别名解析）测试，符合 `design/testing.md`（无真实外部依赖、无墙钟）。
- **文档**：`docs/cli-reference.md` —— 差距表（行 304–310）移除 5 行；「Project」「系统诊断」「Notification」「Server」段落同步新路径；`mo --help` 输出示例（如有）更新。
- **依赖 / API**：无 server 端改动，无 schema 迁移——本 issue 纯 CLI 命令面收敛，所有端点（`/api/status?all=true` / `/api/logs/tail` / `UseProjectAsync` / setup 流程 / `PrintSystemInfoAsync`）保持不变。
- **破坏面**：5 项命令路径变更，现有脚本/CI/远程 SSH 会话中用旧路径的会破。release/changelog 提示；旧路径是否保留过渡别名由实现时统一裁定并记录在 issue 评论。
