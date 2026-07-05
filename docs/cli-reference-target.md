---
status: wip-not-implemented
---

# CLI 命令参考（目标形态）

> **⚠️ 尚未实现**：本文是 `mo` 命令行的**产品 spec**——开发完成的命令面**必须满足**本文的命令、参数与命名约定。当前真实可用的命令以 [`cli-reference.md`](cli-reference.md) 为准，本文多数命令还不存在或与当前不同。落地后内容搬到 `cli-reference.md` 并去掉本提示。

## 怎么读这份 spec

- 每个**必须**（MUST）满足的命令逐条列出，带参数与作用。
- 「迁移」列出的破坏性变更是契约，开发必须按此收敛，不得保留旧路径别名（除非条目本身说明保留别名）。
- 设计原则见 [`design/cli.md`](../design/cli.md)（面向开发者）；本文只写产品面，不写实现。

## 全局约定（所有命令必须遵守）

- **形状**：`mo <资源> <动词>`。资源是名词（issue、epic、workflow），动词是动作（create、get、start）。
- **根命令层只有资源**：`mo` 直接子命令都是资源或资源组，没有裸动词（受控例外仅 `mo info` 一项，见「系统诊断」）。
- **输出格式**：所有 get/list 类命令必须支持 `-o table|json|yaml|compact`。**输出格式不创造命令**——看资源 yaml 用 `mo <资源> get <id> -o yaml`，不存在 `mo <资源> yaml`。
- **项目作用域**：项目内资源必须支持 `--project <名>` 与 `--project-id <id>` 两种形式，缺一不可。
- **预演**：会改状态的控制类命令必须支持 `--dry-run`。
- **别名**：高频命令提供短别名（`ls` = list、`rm` = delete），别名与正名必须同行为。

## 动词词表（全命令面统一，不得跨资源自造同义词）

| 动作类 | 规范动词 | 语义 |
|---|---|---|
| 列表 | `list` | 列出资源的多个实例 |
| 查单条 | `get` | 取一个资源的详情 |
| 新增独立资源 | `create` | 造一个带身份的新资源（project/issue/epic/agent/template） |
| 加集合成员 | `add` | 往现有宿主集合加一项（label 目录项/repo/comment/prereq） |
| 改 | `update` | 修改资源字段 |
| 删除（真删） | `delete` | 永久删除资源（project/repo/template） |
| 归档（软删，可恢复） | `archive` | 归档资源，可逆（issue/agent） |
| 键值查询/设置 | `get`/`set` | 仅 `mo config` 的 KV 范式 |

> 删除与归档是两个动作，不混用：真删除用 `delete`，归档（可恢复）用 `archive`。issue 与 agent 的"删除"实为归档，必须用 `archive`，不得用 `delete`。

## 根命令层（MUST）

```
mo project      项目
mo issue        工作项
mo epic         产品里程碑
mo workflow     工作流执行（WorkflowRun）
mo agent        智能体
mo label        标签
mo runner       执行器
mo server       服务端
mo notification 通知配置
mo config       全局配置（键值）
mo system       系统诊断
mo otel         OpenTelemetry 查询
mo opencode     OpenCode 模型
mo skills       技能分发
mo install      安装（动词根，装机用途）
mo update       升级（动词根，装机用途）
mo info         CLI 本地诊断（受控例外：跨资源只读，不归任一资源）
```

> 当前根上的裸动词 `status`/`logs`/`use`/`notify` 在目标形态归位：`status` → `mo project status`、`logs` → `mo system logs`、`use` 删除（留 `mo project use`）、`notify` → `mo notification`。

## Workflow（工作流执行 / WorkflowRun）

直接以工作流执行 ID（`workflowRunId`）寻址一次具体的工作流执行。这是核心域——驱动一个 issue 从 Draft 走到 Done 的生产线本身。

### 控制动作

| 命令 | 作用 |
|---|---|
| `mo workflow approve <runId>` | 通过审批门 |
| `mo workflow reject <runId> --message <理由>` | 在审批门打回（理由必需） |
| `mo workflow retry <runId>` | 失败后原地重试当前阶段 |
| `mo workflow rerun <runId>` | 从头重跑 |
| `mo workflow rerun <runId> --from-stage <阶段>` | 从指定阶段重跑（变体用 flag，不另造 `rerun-from-stage` 命令） |
| `mo workflow resume <runId>` | 恢复暂停的执行 |
| `mo workflow pause <runId>` | 暂停（可恢复） |
| `mo workflow stop <runId>` | 终止（不可恢复） |

> 这些动作也可通过 issue 号触发（`mo issue approve <编号>`），issue 号是工作流执行的人类可读别名。直接寻址面向脚本与 agent 订阅场景——它们手里只有执行 ID。

### 查询

| 命令 | 作用 |
|---|---|
| `mo workflow get <runId>` `-o table\|json\|yaml` | 执行全貌（含状态、阶段进度、关联 issue、模板定义）。**`-o yaml` 承载模板定义，不单造 `yaml` 命令** |
| `mo workflow status <runId>` | 精简状态摘要（比 get 短） |
| `mo workflow variables <runId>` `[--stage <阶段>] [--key <键路径>]` | 生效变量（子资源，有独立寻址） |
| `mo workflow events <runId>` `[--limit <n>]` | 事件流（关联资源） |
| `mo workflow list-sessions <runId>` | 该执行的会话列表（关联资源，只列出） |

## Workflow Profile（工作流运行配置）

工作流运行配置 = 模板 + 变量 + 提示词覆盖。挂在 project 下（配置是项目拥有的）。目标形态统一到 `mo project workflow profile` 一个资源组，并补齐启用/禁用入口。

```
mo project workflow profile list [--described]      列出 profile（含名称与描述）
mo project workflow profile get                     查看 profile 全貌（默认模板/变量/提示词）
mo project workflow profile set [flags]             复合写入（默认模板/变量/提示词）
mo project workflow profile clear [flags]           复合清除
mo project workflow profile preview <键>            预览渲染后的提示词
mo project workflow profile enable                  启用 profile
mo project workflow profile disable                 禁用 profile
```

> 当前这些操作散在 `mo workflow list`（清单）与 `mo project workflow config`（配置操作）两处，命令名 `config` 与资源 `profile` 名实不符，且启用/禁用无命令入口。目标形态正名为 `profile`、统一到 project 下、补齐 enable/disable。

## Workflow Template（工作流模板）

YAML 定义的模板，project 下管理。当前形态已正确，目标不变。

```
mo project workflow template list                  列出模板
mo project workflow template create --yaml <yaml|@file>
mo project workflow template get <模板id>
mo project workflow template update <模板id> --yaml <yaml|@file>
mo project workflow template delete <模板id>
```

## Project（项目）

```
mo project create <名>
mo project list
mo project get <名或id>
mo project use <名或id>            设置当前项目
mo project delete <名或id>
mo project status                  当前项目聚合状态（当前形态的 mo status 归位到此）
mo project repo ...                仓库管理（见下）
mo project workflow ...            工作流配置（template / profile，见上）
```

### Repository（仓库）

一个 project 可关联多个仓库（如 monorepo 多模块）。**单一入口 `mo project repo`，不再有顶层 `mo repo`。**

```
mo project repo list
mo project repo add <名> --git-url <url> [--base-branch <分支>] [--set-default]
mo project repo update <名> [flags]
mo project repo set-default <名>
mo project repo delete <名>
```

> 仓库是往项目集合加成员，用 `add`（不是 create）。当前 `mo repo` 与 `mo project repo` 双轨并存且参数形状不一，目标形态合并为 `mo project repo` 单一路径。

## Issue（工作项）

```
mo issue create <标题> [options]
mo issue list [options]
mo issue get <编号>
mo issue update <编号> [options]
mo issue start <编号>
mo issue approve <编号>
mo issue reject <编号> --message <理由>
mo issue retry <编号>
mo issue rerun <编号>
mo issue rerun <编号> --from-stage <阶段>      （当前 rerun-from-stage 收敛为 flag）
mo issue stop <编号>                            终止（不可恢复）
mo issue force-stop <编号>                      暂停（可恢复）
mo issue resume <编号>
mo issue rebase <编号>
mo issue close <编号>
mo issue reopen <编号>
mo issue archive <编号>                         归档（软删，可恢复）
mo issue archive --all-completed [options]
mo issue unarchive <编号>
mo issue comment add <编号> [--body <文本>|--body-file <路径>]
mo issue prereq add <编号> <前置编号>
mo issue prereq remove <编号> <前置编号>
mo issue logs <编号>
mo issue events <编号> [--limit <n>]
mo issue diff <编号>
mo issue commits <编号>
mo issue sessions <编号>
mo issue session get <编号> <名称>
mo issue session transcript <编号> <名称>
mo issue session compact <编号> <名称>
mo issue session reset <编号> <名称>
mo issue session followup <编号> <名称> [--text <文本>|--text-file <路径>]
```

> Issue 的工作流快捷方式（approve/retry/rerun/...）是对应 `mo workflow` 命令的人类便利别名，行为一致。prereq 的 `remove` 是历史用法，与 delete 同义，保留为别名。

## Epic（产品里程碑）

```
mo epic create <标题> [--description <描述>] [--priority p0-p3]
mo epic list
mo epic get <id或编号>
mo epic update <id或编号> [options]
mo epic link <epic> <issue>
mo epic unlink <epic> <issue>
mo epic start <id或编号>            开始自动推进
mo epic pause <id或编号>            暂停自动推进
mo epic resume <id或编号>
mo epic done <id或编号>             标记里程碑完成
mo epic close <id或编号>            放弃里程碑
```

> epic 无 delete——里程碑用 done（完成）或 close（放弃）收尾，不删除。

## Agent（智能体）

```
mo agent create [options]
mo agent list
mo agent get <名或id>
mo agent update <名或id> [options]
mo agent archive <名或id>           归档（目标形态：当前叫 delete，名实不符，正名为 archive）
mo agent session list <agent>
mo agent session get <会话id>
mo agent session transcript <会话id>
mo agent session launch <agent> [--prompt <文本>|--prompt-file <路径>]
mo agent session followup <会话id> [--text <文本>|--text-file <路径>]
mo agent session cancel <会话id>
```

## Label（标签）

```
mo label list                       标签目录
mo label add <键> [--description <文本>] [--supported-values <v1,v2>]
mo label update <键> [options]
mo label delete <键>                目标形态：当前叫 remove，统一为 delete
```

> label 是往项目标签目录加定义，用 `add`（不是 create）。`remove` 与 `delete` 同义，`delete` 为正名、`remove`/`rm` 为别名。

## Runner（执行器）

```
mo runner list [--scope all|global|project]
mo runner get <执行器id>
mo runner status                    在线执行器摘要
```

执行器的安装/启停见「安装与升级」。

## Server（服务端）

```
mo server health                    健康检查
mo server info                      服务端系统诊断（目标形态：当前 mo system info 归位到此，消歧）
mo server status                    服务状态
mo server logs                      服务日志（受管服务的运维日志）
```

> `mo system logs`（应用日志）与 `mo server logs`（运维日志）内容不同，前者是应用输出、后者是 systemd/计划任务层日志。

## 系统诊断（只读）

```
mo info                             CLI 本地环境与安装来源（受控例外：跨资源只读）
mo system logs                      应用日志（当前形态的 mo logs 归位到此）
mo otel query <sql> [--db <路径>]   直接查询 OpenTelemetry 数据库（无需服务端）
mo otel status                      OpenTelemetry 采集器状态（需服务端）
mo opencode models                  当前项目可用模型
```

## Notification（通知配置）

```
mo notification setup               通知平台配置向导（当前形态的 mo notify setup 归位到此）
```

## 配置（键值）

```
mo config list
mo config get <键>
mo config set <键> <值>
```

> `mo config get/set` 是键值范式，与资源 `get` 语义不同但词相同——KV 查询是 CLI 通用惯例（如 git config），保留。

## 技能分发

```
mo skills list
mo skills install
mo skills get <名>
mo skills path <名>
mo skills sync
```

> 目标形态：`mo skills` 当前用 `--json` 布尔开关，必须统一到 `-o json`（全局输出格式约定）。

## 安装与升级（动词根集中）

**单一归属：动词根 `mo install` / `mo update`**，不再有 `mo server install/update`、`mo runner install/update` 与之并存。

```
mo install server                   安装服务端为受管服务
mo install runner                   安装执行器为受管服务

mo update                           升级全部（CLI + 服务端 + 执行器）
mo update cli                       仅升级 mo CLI
mo update server                    仅升级服务端
mo update runner                    仅升级执行器
```

> 当前 `mo server install/update`、`mo runner install/update` 与上述四条等价并存，目标形态删除资源下的重复入口，只留动词根。

## 迁移（破坏性变更，开发必须按此收敛）

| 当前 | 目标 | 性质 |
|---|---|---|
| `mo workflow list` | `mo project workflow profile list` | 路径变更（profile 让位给 WorkflowRun） |
| `mo project workflow config ...` | `mo project workflow profile ...` | 正名（config → profile） |
| `mo repo ...` | `mo project repo ...` | 双轨合并（删顶层） |
| `mo agent delete` | `mo agent archive` | 正名（delete → archive，名实相符） |
| `mo label remove` | `mo label delete`（remove 转别名） | 词表统一 |
| `mo server install/update`、`mo runner install/update` | `mo install/update` | 双入口合并（删资源下重复） |
| `mo issue rerun-from-stage` | `mo issue rerun --from-stage` | 命令收敛为 flag |
| `mo status` | `mo project status` | 裸动词归位 |
| `mo logs` | `mo system logs` | 裸动词归位 |
| `mo use` | （删除，留 `mo project use`） | 重复入口删除 |
| `mo notify setup` | `mo notification setup` | 资源化 |
| `mo system info` | `mo server info` | 消歧（与 `mo info` 区分） |
| `mo <资源> show` | `mo <资源> get` | 词表统一（show → get） |

纯增量（不破坏现有脚本）：

- `mo workflow <control> <runId>` 8 个控制动作 —— 新增
- `mo workflow <read> <runId>` 5 个查询 —— 新增
- `mo project workflow profile enable/disable` —— 新增

## 不在本文范围

- 实现细节（服务端接口、命令构造）—— 见 `design/cli.md`（面向开发者）。
- 当前实装的完整命令清单 —— 见 `cli-reference.md`，以代码为准。
- 词表里未列出的领域生命周期动作（approve/reject/retry/rerun/resume/pause/stop/start/link/unlink/done/close/archive/enable/disable 等）按领域语义命名，不套 CRUD，各资源组正文已逐一定明。
