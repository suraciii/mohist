## Context

`design/cli.md`「唯一入口」定明：同一能力只有一条命令路径，等价命令并存即契约违背。装机与升级是"系统级动词"，`docs/cli-reference.md`「安装与升级」段已裁定归属为**动词根集中**（`mo install <组件>` / `mo update [组件]`）。

但当前 `mo server` / `mo runner` 资源组仍挂着与动词根完全等价的 `install` / `update` 子命令，打同一组 `IServiceInstaller` / `SourceCodeUpdater` 方法：

| 动词根（留） | 资源下（删） | 共用方法 |
|---|---|---|
| `mo install server` | `mo server install` | `IServiceInstaller.InstallServerAsync` |
| `mo install runner` | `mo runner install` | `IServiceInstaller.InstallRunnerAsync` |
| `mo update server` | `mo server update` | `SourceCodeUpdater.UpdateServerAsync` |
| `mo update runner` | （代码中从未注册） | `SourceCodeUpdater.UpdateRunnerAsync` |

没有任何一方独有的 flag 或语义差异。这是命令面收敛批次（epic #41）里收敛装机动词的一步，紧跟 issue-387（根命令裸动词归位）之后——两者共享同一设计契约（"唯一入口"）与同一处理范式（删冗余入口、不保留 alias）。

约束 / 利益相关方：
- 纯 CLI 命令面收敛：无 server 端改动、无 schema 迁移、无 `IServiceInstaller` / `SourceCodeUpdater` 方法签名变化。
- 资源组下的非装机子命令（start/stop/restart/status/logs/health/uninstall/info 及 runner 的 list/show/service-status）必须原样保留。
- `mo runner update` 在当前代码中**本就不存在**（`RunnerCommands.Build` 从未注册 `update`）——这不是要删的代码，而是要**保持的不变量**。

## Goals / Non-Goals

**Goals:**
- 删除 `mo server install`、`mo server update`、`mo runner install` 三条冗余命令路径，使装机/升级只走动词根。
- 动词根入口（`mo install server/runner`、`mo update[/cli/server/runner]`）的行为、flag 集、退出码与输出语义完全不变。
- 保持 `mo runner update` "从不存在"的不变量，并加回归守卫防止未来被当作 alias 静默加回。
- `mo server` / `mo runner` 的非装机子命令原样可用。
- `docs/cli-reference.md` 与收敛后的命令面对齐（删差距行、删资源段里的装机/升级行、删迁移提示）。

**Non-Goals:**
- 不动 `mo server` / `mo runner` 的任何非装机子命令。
- 不改 `mo install` / `mo update` 动词根的行为、flag 或 stage-machine。
- 不处理根命令其它裸动词归位（issue-387 的范畴）。
- 不为 install/update 加新能力（如 `mo install all`）。
- 不保留 alias / 过渡期兼容（见 D1）。

## Decisions

### D1 — 纯删除，不保留 alias（与 issue-387 D1 同构）

删除 `ServerCommands.Build` 里 `BuildInstall` / `BuildUpdate` 两个私有方法及其 `Subcommands.Add` 注册，删除 `RunnerCommands.Build` 里的 `BuildInstall` 及其注册。被删路径调用时由 System.CommandLine 返回解析失败 + 非零退出，不产生任何装机/升级副作用。

**不保留 alias 的理由：**
- 与 issue-387 的 D1「uniform no-alias policy」一致——同一契约定下的收敛用同一范式，避免出现"根命令裸动词删了不留 alias、资源下装机删了却留 alias"的双标。
- AGENTS.md 自己的示例就是动词根形态（`mo update server` / `mo update runner`），说明动词根是推荐用法，资源下入口是冗余而非在用主流。
- 留 alias 会把"唯一入口"契约永久打折扣，且需要额外的 alias 维护/测试成本。

**Alternative considered:** 保留 `mo server install` 等为隐藏 alias，仅从 `--help` 移除。否决——隐藏 alias 仍是第二入口，违背契约；且隐藏命令更难被脚本作者发现其将被废弃，反而放大破坏面。

### D2 — 死代码清理边界（精确到变量级）

删除两个 `BuildInstall` + 一个 `BuildUpdate` 后，需检查各自 `Build` 方法里为它们准备的局部变量是否变成孤儿：

- **`ServerCommands.Build`**：`installer`（行 12）仍被 `BuildSystemd`(start/stop/restart/status/uninstall) 与 `BuildLogs` 使用——**保留**；`updater`（行 13）仅被 `BuildUpdate` 使用——**删除**。
- **`RunnerCommands.Build`**：`installer`（行 140）仍被 `BuildSystemd`(start/stop/restart/service-status/uninstall) 与 `BuildLogs` 使用——**保留**；本方法无 `updater` 局部变量——无清理。
- **`InstallCommands` / `UpdateCommands`**：完全不改，它们是保留的动词根入口。

`TreatWarningsAsErrors` 会把任何遗漏的未使用变量变成编译失败，故死代码清理的回归由编译器兜底。

### D3 — `mo runner update` 不变量用显式回归守卫固化

`mo runner update` 在代码中从未注册（`RunnerCommands.Build` 行 137-159 只 `Add(BuildInstall(installer))`，无 update）。本 issue 无代码可删，但 update-side spec 把它列为显式要求——目的是让"执行器升级只走 `mo update runner`"这个对称不变量**被测试钉死**，防止未来某次"补全资源下 update 子命令"的改动把它当 alias 静默加回。

落地：新增一条 spec，断言 `mo runner update` 解析失败、非零退出、不触发任何 `SourceCodeUpdater` 方法（与 D4 的 Legacy 模式同形）。

### D4 — 测试策略：复用 issue-387 的 Legacy* 模式

issue-387 的 `CliRootCommandShapeSpecs.cs` 已建立"删路径"的标准验证形状：调用 `MohistCliCommands.RunAsync` 跑被删路径 → 断言 `exitCode != 0` 且 `handler.Requests` 为空（解析失败、无副作用）。本 issue 直接复用：

- **被删路径守卫**：`mo server install` / `mo server update` / `mo runner install` 三条，各一例，断言解析失败 + 非零退出 + installer/updater 未被调用。installer 是否被调用通过 fake（`Support/FakeServiceInstaller.cs` 已存在）观测；updater 通过断言无 HTTP 请求 / 无命令执行观测。
- **`mo runner update` 不变量守卫**（D3）：同形一例。
- **动词根行为不变守卫**：`mo install server/runner`、`mo update server/runner` 各保留/新增一例，断言仍命中同一个 installer/updater 方法（用 fake 记录调用参数）。
- **现存正向 help 断言翻转**：`CliRunnerCommandSpecs` 当前有**两处** `Assert.Contains("install", stdout)` 断言跑 `mo runner --help`，合并后都须改为 `DoesNotContain`，否则会编译过但测试红。这是本 issue 最容易遗漏的回归点：
  - `RunnerHelp_ListsListSubcommand`（行 61）
  - `RunnerShow_HelpText_ListsShowAndExistingSubcommands`（行 665）
  
  两处都断言 `mo runner --help` 广告 `install`，合并后 `install` 不再被广告，故两处都必须翻转。
- **资源组子命令存活守卫**：`mo server --help` / `mo runner --help` 断言非装机子命令仍被广告、install/update 不被广告（install/update-single-entry spec 已列）。

**Alternative considered:** 只跑 `--help` 文本断言，不跑被删路径的解析失败用例。否决——help 文本是可发现性信号，但"调用被删路径不产生副作用"是契约的核心，必须有用例钉死（与 issue-387 同标准）。

### D5 — 文档同步：差距收敛，迁移提示一并删

`docs/cli-reference.md` 三处改动，由 `CliReferenceDocsSpecs.cs` 的 forbidden-legacy-row 模式（行 90-101）守护：

1. **差距表（行 340）**：删 `mo server install/update` / `mo runner install/update` → `mo install/update` 双入口合并那一行——差距本 issue 关闭。
2. **Server 段代码块（行 271-272）**：删 `mo server install` / `mo server update` 两行。
3. **Runner 段代码块（行 255）**：删 `mo runner install` 一行。
4. **命令路径迁移提示（行 278）**：删——该提示跟踪的就是本 issue 关闭的差距，差距关闭后提示失去存在理由。

「安装与升级（动词根集中）」段（行 322-331）已是目标形态，不改。AGENTS.md / `docs/self-host.md` / `docs/hermes-notifications.md` 用的是动词根形态，无需改。

## Risks / Trade-offs

- **[破坏使用被删路径的脚本/CI/SSH 会话]** → release/changelog 提示迁移到动词根 `mo install <组件>` / `mo update <组件>`。无数据/状态副作用（删除发生在命令注册层，被删路径调用即解析失败，不会留下半完成的装机/升级），破坏是"命令找不到"而非"行为异常"，用户侧诊断成本低。
- **[遗漏对被删私有方法的引用]** → `TreatWarningsAsErrors` + 编译失败兜底；私有方法 `BuildInstall` / `BuildUpdate` 仅在各自 `Build` 内被引用，无外部消费者。
- **[正向 help 断言翻转遗漏（D4）]** → `CliRunnerCommandSpecs` 现有两处 `Contains("install")` 断言（行 61、行 665）在合并后会红；这是强制信号而非静默回归。实现时须主动把两处都改为 `DoesNotContain`。
- **[`mo runner update` 未来被误加回]** → D3 的显式不变量守卫会在 CI 第一时间报红。
- **[文档与代码漂移]** → `CliReferenceDocsSpecs` 的 forbidden-legacy-row 守卫会阻止差距行/迁移提示/资源段装机行残留。

## Migration Plan

单 PR、单次部署、无数据迁移。编辑顺序（每步均可独立编译）：

1. **删 `ServerCommands` 装机/升级入口** —— 删 `BuildInstall`（行 58-78）与 `BuildUpdate`（行 80-94）两个私有方法、两条 `Subcommands.Add`（行 16-17）、以及变成孤儿的 `var updater = ...`（行 13）。`installer` 局部保留（仍被 systemd/logs 用）。
2. **删 `RunnerCommands` 装机入口** —— 删 `BuildInstall`（行 295-318）与 `runner.Subcommands.Add(BuildInstall(installer))`（行 143）。`installer` 保留。
3. **测试** —— 翻转 `CliRunnerCommandSpecs` 的 `install` help 断言；新增 3 条被删路径解析失败守卫 + 1 条 `mo runner update` 不变量守卫；补/保留动词根行为不变断言；`CliReferenceDocsSpecs` 加被删路径到 forbidden 列表。
4. **文档** —— `docs/cli-reference.md` 删差距行、Server/Runner 段装机行、迁移提示。

**验证门槛：** `npm test -w packages/cli`（含 `TreatWarningsAsErrors`）全绿——被删路径解析失败用例、动词根行为不变用例、help 形状用例、文档守卫共同构成回归断言。

**回滚：** revert 单 PR。无持久化数据影响——本 change 不触碰任何存储/状态，被删路径在合并前后都不写 unit 文件、不重建服务、不重启 runner（合并前它们能执行，回滚后恢复可执行；合并后它们解析失败，回滚即恢复）。

## Open Questions

无。proposal 与两份 spec 已把命令面、行为不变量、文档差距全部钉死；`mo runner update` 的"从未存在"状态经代码核对确认。唯一需在实现时用 `rg` 快速复核的是：确认被删的 `BuildInstall` / `BuildUpdate` 私有方法没有被同文件外的测试/反射间接引用（预期没有，因为它们是 `private static`）。
