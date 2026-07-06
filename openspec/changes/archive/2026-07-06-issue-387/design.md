## Context

`design/cli.md` 「命令形状」定明：根命令层只接受资源/资源组，不接受裸动词；唯一受控例外是跨资源全局只读诊断（`mo info`）。当前根上挂着 5 个违背契约的入口，它们各自其实属于某个资源：

| 当前根入口 | 实际归属 | 违背点 |
|---|---|---|
| `mo status` | project | 裸动词（`MohistCliCommands.cs:219`，打 `/api/status?all=true`） |
| `mo logs` | system | 裸动词（`MohistCliCommands.cs:226`，打 `/api/logs/tail`） |
| `mo use <project>` | project | 重复入口（`MohistCliCommands.cs:233`，与 `Project.cs:79` 共用 `UseProjectAsync`） |
| `mo notify setup` | notification | 动词名组（`Notify.cs:63`，应是名词资源组） |
| `mo system info` | server | 命名冲突（`System.cs:19`，与受控例外 `mo info` 同名易混） |

五项都是**命令路径迁移**，端点、输出、flag 语义一律不变。无 server 端改动，无 schema 迁移。

### Current state (verified by code reading)

- 根 `Build` 在 `MohistCliCommands.cs:14-34` 注册了 20 个直接子命令；其中行 14、15、24 是本次要移除的裸动词（`status`/`logs`/`use`），行 33 是要改名的 `notify` 组。
- `SystemCommands.Build`（`System.cs:8-17`）当前只挂 `info` 一个子命令；迁出 `info` 后该组将变空，但本次会迁入 `logs`，组得以保留并改述为应用诊断组。
- `ServerCommands.Build`（`Server.cs:9-26`）已挂 9 个子命令，本次新增 `info` 作为第 10 个。
- `NotifyCommands`（`Notify.cs`，668 行）是个大类，`Build`（行 61-66）只造 `notify` 组挂 `setup`；所有 setup 逻辑（`RunSetupAsync`、probe、写配置、打印订阅命令）是静态方法，**与组名无耦合**——改名只动 `Build` 的 `"notify"` 字面量与 XML doc 注释。
- 测试现状：根级 `mo status`/`mo logs`/`mo use` **没有专用的 spec 测试**（它们是 1 行 handler，迁移风险在测试侧极低，主要是新增而非迁移）；`mo system info` 有 7 个 spec（`CliSystemInfoCommandSpecs.cs`，调用 `["system","info",...]`）；`mo notify setup` 有大量 spec（`CliNotifySetupCommandSpecs.cs`，650 行，调用 `["notify",...]`）。
- `CliReferenceDocsSpecs.cs:78-123` 有一个 doc-sync 测试显式断言 `docs/cli-reference.md` 含 `mo status`/`mo logs`/`mo system info`/`mo use <project>`——这是文档与代码的绑定闸门，本次必须同步改。
- `docs/cli-reference.md:303-310` 的实装差距表恰好列了本次 5 行中的 4 行（`mo use` 那行也在）。
- 项目立场（`AGENTS.md:7`）：「本项目正处在积极开发过程中，无需考虑版本兼容」。这是别名策略决策的关键输入。

### Constraints / stakeholders

- 纯 CLI 命令面收敛；server / runner / schema 零改动。
- 触碰 CLI 公共契约 + 5 处命令路径变更，risk=medium（来自 issue）。
- 文档与代码必须同步——`CliReferenceDocsSpecs` 是闸门，改完代码必须让该测试仍绿。
- 破坏面是旧路径脚本/CI；项目已声明不考虑版本兼容。

## Goals / Non-Goals

**Goals:**

- **G1**：根命令层契约成立——`mo --help` 只列资源/资源组 + 受控例外 `mo info`，五个裸动词/错名路径全部从根消失。
- **G2**：五项归位后的新路径**逐字节复刻**原行为（同端点、同输出、同 exit code、同 flag、同错误文案）——这是 spec 的硬约束（每项 spec 都有 "reproduces ... exactly" requirement）。
- **G3**：旧路径别名策略**统一裁定并记录在 issue 评论**（`root-command-shape` spec 的强制要求）。
- **G4**：每项改动带 CLI 测试，符合 `design/testing.md`（无真实外部依赖、无墙钟）；新增根层形状守卫测试，防止未来回退。
- **G5**：`docs/cli-reference.md` 差距表移除 5 行，命令组段落与 `mo --help` 示例同步新路径。

**Non-Goals:**

- 不改各命令的内部行为（端点、输出、flag 语义不变）——spec 反复强调 "pure path relocation"。
- 不动 `mo info`（受控例外保留）。
- 不合并 `mo install/update` 双入口（另一个 issue）。
- 不为 `mo notification` 加 setup 之外的子命令。
- 不评估 `mo project workflow template/profile` 扁平化（epic #40 开放问题）。

## Decisions

### D1 — 别名策略：统一直接迁移，不保留任何旧路径别名

**Decision.** 五项全部**直接迁移、不保留别名**。`mo status`/`mo logs`/`mo use`/`mo notify setup`/`mo system info` 在改动后即不再解析，调用方收到解析错误并退出非零。该决策记录在 issue 评论（满足 `root-command-shape` spec 的"统一策略须记录"硬要求）。

**Rationale.** 项目已声明「无需考虑版本兼容」（`AGENTS.md:7`）。保留别名会带来三个代价：(1) 5 条旧路径的双路径测试面（每条都要验证别名与规范路径字节一致）；(2) 别名在 `mo --help` 里要么泄露（破坏根层契约，让裸动词仍可发现）要么隐藏（System.CommandLine 的 `Aliases` 不会列进 help，但命令仍解析，契约形改实存）；(3) 别名是无期限遗产，与"积极开发中收敛命令面"的方向相反。"全不保留"是平凡满足 spec 的"五项统一"要求的唯一零成本方案。

**Alternatives considered.**

- *A1（被拒）— 五项全保留为别名。* 让 `mo status` 仍解析但内部转发到 `mo project status`。违背"无需版本兼容"立场，翻倍测试面，且让根层契约在别名存活期间持续被破。拒绝。
- *A2（被拒）— 部分保留（如只给高频的 `mo status` 留别名）。* 直接违反 `root-command-shape` spec 的"五项策略必须统一"要求。拒绝。
- *A3（选用）— 五项全不保留。* 与项目立场一致，零别名维护成本，根层契约立即成立。

### D2 — 迁移机制：搬动 `Build*` 工厂方法，handler 一字不改

**Decision.** 每项迁移是机械的工厂方法搬迁：把 `Build*` 从原属类移到目标资源组类，在目标组的 `Build` 里 `Subcommands.Add`，从根 `Build` 删掉对应行。所有 handler（`api.PrintGetAsync(...)` / `api.UseProjectAsync(...)` / `api.PrintSystemInfoAsync(...)` / `RunSetupAsync(...)`）保持原样。

逐项落点：

| 迁移 | 源 | 目标 | handler |
|---|---|---|---|
| `status` | `MohistCliCommands.BuildStatusCommand` (私有, `:219`) | `ProjectCommands.BuildStatus` (新增) | `api.PrintGetAsync("/api/status?all=true")` |
| `logs` | `MohistCliCommands.BuildLogsCommand` (私有, `:226`) | `SystemCommands.BuildLogs` (新增) | `api.PrintGetAsync("/api/logs/tail")` |
| `use` | `MohistCliCommands.BuildUseCommand` (私有, `:233`) | 删除（`ProjectCommands.BuildUse` `:79` 是同一 handler 的幸存入口） | `api.UseProjectAsync(id)` |
| `notify`→`notification` | `NotifyCommands.Build` (`:61`) 改组名字面量 | 同文件，组名 `"notify"`→`"notification"` | `RunSetupAsync(...)` 不变 |
| `system info`→`server info` | `SystemCommands.BuildInfo` (`:19`) | `ServerCommands.BuildInfo` (新增) | `api.PrintSystemInfoAsync(mode)` 不变 |

根 `Build`（`MohistCliCommands.cs:14-34`）：删行 14、15、24；行 33 的 `NotifyCommands.Build(api)` 调用不变（改名在 `NotifyCommands` 内部）。

**Rationale.** 这是风险最低、可逆性最好的改法：handler 与 `MohistCliApi` 的契约完全不动，端点字符串原样搬，任何行为差异都会在迁移测试里立刻暴露。System.CommandLine 的 `Command` 是纯构造对象，`Subcommands.Add` 顺序不影响解析。

**Alternatives.** *引入一层 indirection（如 `BuildStatusAt(api, Command parent)` 让根和组都能挂）*——被拒，因为 D1 已决定不留别名，单挂点足够，多挂点反而暗示双入口合理。

### D3 — `system` 组迁入 `logs` 并改述为"应用诊断组"，不留空壳

**Decision.** `SystemCommands.Build` 在迁出 `info` 后立即迁入 `logs`，组描述改为点明它的 `logs` 是**应用日志**（Mohist server 自身的 log tail），并与 `mo server logs`（systemd/计划任务层的运维日志）显式区分。该区分写入组描述，让 `mo system --help` 的读者能一眼分清两个日志面。

**Rationale.** `system-logs` spec 显式要求"system 组描述须记录应用日志 vs 运维日志的区分"。若 `system` 只迁出 `info` 不迁入任何东西，它会变成空组（无子命令），既不符 spec 又让 `mo system --help` 无内容。迁入 `logs` 让组有实质且语义自洽：`system` = 应用层诊断，`server` = 服务管理（含运维日志）。

**Alternatives.** *A1（被拒）— 删掉空的 `system` 组。* 但 `logs` 无家可归，且 spec 要求 `system` 组存在。*A2（被拒）— 把 `logs` 也放进 `server`。* 那会让 `mo server logs`（运维）和 `mo system logs`（应用）合并，破坏 spec 的区分要求。

### D4 — 测试策略：改写调用参数 + 新增根层形状守卫

**Decision.** 分三类测试改动：

1. **改写调用参数**（现有 spec 的 args 数组）：
   - `CliSystemInfoCommandSpecs.cs`：所有 `["system","info",...]` → `["server","info",...]`；`System_Help_ListsInfoSubcommand` → `Server_Help_ListsInfoSubcommand`；新增 `System_Help_NoLongerListsInfo`（反向断言）。
   - `CliNotifySetupCommandSpecs.cs`：所有 `["notify",...]` → `["notification",...]`；`NotifyRoot_Help_ListsSetupSubcommand` → `NotificationRoot_Help_ListsSetupSubcommand`。
2. **新增 spec**（新路径的行为复刻 + 帮助）：
   - `CliProjectStatusCommandSpecs`：`mo project status` 打 `/api/status?all=true`、server-unreachable 退出非零、`mo project --help` 列 `status`、`mo project status --help` 不含 `--project`/`--project-id`。
   - `CliSystemLogsCommandSpecs`：`mo system logs` 打 `/api/logs/tail`、`mo system --help` 列 `logs` 且描述区分应用/运维日志。
   - `mo server info --help` 描述区分 `mo info`（已有断言模式，搬迁即可）。
3. **新增根层形状守卫 spec**（`CliRootCommandShapeSpecs`，新文件）：
   - `mo --help` 不含 `status`/`logs`/`use`/`notify`，含 `project`/`system`/`server`/`notification`/`info`。
   - `mo status`/`mo logs`/`mo use <x>`/`mo notify setup`/`mo system info` 解析失败、退出非零（D1 的直接验证）。
   - `mo info` 不变（受控例外，输出与改动前一致——用同一个断言快照）。

**Rationale.** 现有根级裸动词没有专用 spec，所以"迁移"主要是新增；`system info`/`notify setup` 的 spec 数量大但改动机械（args 字面量替换）。根层形状守卫是 G4 的回归防线——一旦未来有人重新在根上挂裸动词，该测试立刻红。

**Alternatives.** *只改 args 不加根层守卫*——被拒，因为契约本身（根只接受资源）没有测试就会再次漂移，这正是本次 issue 的成因。

### D5 — 文档同步：差距表移除 5 行 + 全仓 prose 扫描

**Decision.** 两步：

1. **`docs/cli-reference.md`**（闸门文档）：删除差距表 5 行（`:306-310`）；命令组段落（Project / 系统诊断 / Notification / Server）同步新路径；`mo --help` 输出示例块更新为只含资源 + `info`。
2. **全仓 prose 扫描**（其它含旧路径的文档，已定位）：
   - `docs/concepts.md`（`mo use my-app` / `mo status`）、`docs/getting-started.md`（`mo use my-app`）、`docs/runner.md`（`mo status` / `mo logs`）、`docs/troubleshooting.md`（`mo status`×2 / `mo logs`×2）、`docs/issues.md`（`mo status`）、`docs/hermes-notifications.md`（`mo notify setup`×3）。
   - skill-data（`packages/cli/Mohist.Cli/skill-data/`）已确认**不含**旧路径，无需改动。

**Rationale.** `CliReferenceDocsSpecs.cs:78-123` 是文档与代码的绑定闸门——它显式断言 `cli-reference.md` 含哪些顶级命令。本次必须把该测试的期望列表（`mo status`/`mo logs`/`mo system info`/`mo use <project>`）替换为新规范路径，否则测试红。其它文档没有测试闸门，靠一次性 prose 扫描收敛，避免遗留错误示例误导用户。

**Alternatives.** *只改 `cli-reference.md`，其它文档留着*——被拒，因为 `concepts.md`/`getting-started.md` 是新用户入口，留旧路径会立刻让人踩空。

## Risks / Trade-offs

- **[破坏旧路径脚本/CI]** 5 条路径直接失效，无别名过渡。 -> 项目声明「无需版本兼容」；D1 已裁定；release/changelog 提示破坏性变更。最大破坏面是 `mo status`（高频），用户改脚本为 `mo project status`。
- **[文档漂移]** 除 `cli-reference.md` 外的 6 个文档无测试闸门，prose 扫描可能漏改。 -> 实现时用 `rg --fixed-strings 'mo status|mo logs|mo use|mo notify|mo system info' docs/` 复核；改动同提交闭合。
- **[`system` 组语义被误读为"系统运维"]** 用户可能以为 `mo system logs` 是 systemd 日志。 -> D3：组描述显式写明应用日志 vs `mo server logs` 运维日志的区分，`system-logs` spec 有对应断言。
- **[测试类改名掩盖了迁移历史]** 把 `CliSystemInfoCommandSpecs` 改名为 `CliServerInfoCommandSpecs` 会让 git blame 断链。 -> 保留原类名只改 args，或改名时在 commit message 列出映射；优先保留类名减改动面。
- **[根层形状守卫测试可能过严]** 断言 `mo --help` 不含某些词可能误伤未来合法的同名资源。 -> 守卫断言的是"这 5 个具体旧路径不解析"，不是"根上不能有任何新动词"；未来新增资源不受影响。

## Migration Plan

- **无 schema/API 迁移。** 纯 CLI 命令面收敛；server/runner 零改动；无新端点、字段或存储。
- **单提交闭合。** 代码（5 个 `Build*` 搬迁 + 根 `Build` 删行）+ 测试（改写 args + 新增 3 个 spec 文件）+ 文档（`cli-reference.md` 差距表 + 6 个 prose 文件）在同一提交，由 `CliReferenceDocsSpecs` 保证 doc/code 同步。
- **部署顺序。** 只发 CLI（`mo update cli`）；server/runner 不受影响。
- **回滚。** 单提交 revert 即恢复旧路径；无持久状态变更。
- **合并后。** 在 issue 评论记录 D1 决策（"五项统一不保留别名，理由：项目声明无需版本兼容"），满足 `root-command-shape` spec 的强制记录要求。

## Open Questions

- **`system` 组的未来边界？** 它现在只挂 `logs`（应用日志）。未来 `mo otel status`（应用可观测）是否应迁入 `system`？本次不定，留 epic #41 评估。
- **`docs/` prose 扫描是否覆盖所有调用点？** 已用 ripgrep 定位 6 个文件，但实现时须重新跑一次确认（评论/代码块里可能有漏网）。`CliReferenceDocsSpecs` 只闸门 `cli-reference.md`，其它文档靠人工闭合。
- **`NotifyCommands` 类名是否要改成 `NotificationCommands`？** 类名是内部的，不影响命令面；为减改动面可保留类名只改组名字面量。若 review 倾向彻底改名，代价是 `CliNotifySetupCommandSpecs` 的 `NotifyCommands.HealthProbeOverride` 等静态引用全改。倾向保留类名（D2 的"handler 不动"延伸）。
