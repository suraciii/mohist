# CLI 参考

`mo` 是 Mohist 的命令行入口，面向脚本、自动化和远程 SSH 场景。本文是 `mo` 命令面的**产品 spec**——命令面必须满足本文的命令、参数与命名约定。设计原则见 [`design/cli.md`](../design/cli.md)（面向开发者）。首次安装上手走 [快速上手](getting-started.md)。

> 本文先于实现。命令面随各命令组的改进 issue 逐步对齐到本文；未对齐处以代码为实装事实，但本文是目标，实装去追赶 spec，而非相反。

## 全局约定

- **形状**：`mo <资源> <动词>`。资源是名词（issue、epic、workflow），动词是动作（create、get、start）。
- **根命令层只有资源**：`mo` 直接子命令都是资源或资源组，没有裸动词（受控例外仅 `mo info` 一项，见「系统诊断」）。
- **输出格式**：所有 get/list 类命令支持 `-o table|json|yaml|compact`。**输出格式不创造命令**——看资源 yaml 用 `mo <资源> get <id> -o yaml`，不存在 `mo <资源> yaml`。
- **项目作用域**：项目内资源支持 `--project <名>` 与 `--project-id <id>` 两种形式，缺一不可。作用域用 flag 表达（对标 `--namespace`），不进命令路径——项目作用域内的资源在顶层有自己的命令组，通过 `--project` 限定，不嵌套在 `mo project` 下（仓库、issue、epic、agent 等都是如此）。
- **预演**：会改状态的控制类命令支持 `--dry-run`。
- **别名**：高频命令提供短别名（`ls` = list、`rm` = delete），别名与正名同行为。

### 输出格式与项目作用域 flag

资源子命令的 `-o` 与 `--project` 覆盖范围：

- `list`、`show` 和 session 子命令支持 `-o table|json`。
- `list` 支持 `-o table|json`；所有子命令支持 `--project`/`--project-id`。
- 项目作用域命令通常接受 `--project <name>` 和 `--project-id <id>`，但**并非**所有子命令都同时接受两者——`--project` 走名解析、`--project-id` 走 id 解析，缺哪个就报参数错。具体每个命令的 flag 集合以 `mo <命令> --help` 为准。

`--project` 与 `--project-id` 互斥——同一个命令不能同时传两个，会本地失败、不发请求。

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

删除与归档是两个动作，不混用：真删除用 `delete`，归档（可恢复）用 `archive`。issue 与 agent 的"删除"实为归档，用 `archive`，不用 `delete`。

## 根命令层

```
mo project      项目
mo repo         仓库
mo issue        工作项
mo epic         产品目标
mo workflow     工作流执行（WorkflowRun）
mo event        事件投递运维
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

## Workflow（工作流执行 / WorkflowRun）

直接以工作流执行 ID（`workflowRunId`）寻址一次具体的工作流执行。这是核心域——驱动一个 issue 从 Draft 走到 Done 的生产线本身。

### 控制动作

| 命令 | 作用 |
|---|---|
| `mo workflow approve <runId>` | 审批通过 |
| `mo workflow reject <runId> --message <理由>` | 审批打回（理由必需） |
| `mo workflow retry <runId>` | 失败后原地重试当前阶段 |
| `mo workflow rerun <runId>` | 从头重跑 |
| `mo workflow rerun <runId> --from-stage <阶段>` | 从指定阶段重跑（变体用 flag，不另造 `rerun-from-stage` 命令） |
| `mo workflow resume <runId>` | 恢复暂停的执行 |
| `mo workflow pause <runId>` | 暂停（可恢复） |
| `mo workflow stop <runId>` | 终止（不可恢复） |

这些动作也可通过 issue 号触发（`mo issue approve <编号>`），issue 号是工作流执行的人类可读别名。直接寻址面向脚本与事件路由触发的 Mohist Agent——它们手里只有执行 ID。

所有控制命令支持 `-o table|json` 和 `--dry-run`（打印请求体不发请求）。issue 快捷方式同样使用 `mo issue rerun <编号> --from-stage <阶段>`；`pause` 可恢复，`stop` 是终态。

### 查询

| 命令 | 作用 |
|---|---|
| `mo workflow get <runId>` `-o table\|json\|yaml` | 执行全貌（含状态、阶段进度、关联 issue、模板定义）。默认 table 是摘要视图，`-o json/yaml` 是全貌。`-o yaml` 承载模板定义，不单造 `yaml` 命令 |
| `mo workflow variables <runId>` `[--stage <阶段>] [--key <键路径>]` | 生效变量（子资源，有独立寻址） |
| `mo workflow events <runId>` `[--limit <n>]` | 事件流（关联资源） |
| `mo workflow list-sessions <runId>` | 该执行的会话列表（关联资源，只列出） |

`get` 的资源响应包含关联 issue 的 number 与 title，可直接用于把 run 关联回 issue，无需额外 lookup。单 session 子动作（get / transcript / compact / reset / followup）的 workflowRunId 直接入口不在本命令组，继续走 `mo issue session ...`。

## Event（事件投递运维）

事件投递失败并耗尽自动重试后进入 dead letter。运维入口只连接本机 Mohist 服务，查询结果默认显示恢复状态，避免把正在重投的记录误当成可再次重试的记录。

| 命令 | 作用 |
|---|---|
| `mo event dead-letter list [--handler <处理者>] [--limit <1-500>] [-o table\|json]` | 列出尚未解决的 dead letter；table 包含 `status`、尝试次数和安全摘要 |
| `mo event dead-letter redeliver <id> [-o table\|json]` | 只重投该记录中失败的处理者；成功后将记录标为已解决 |

服务端与 `mo` 共用同一 operator credential，按以下顺序解析：

1. `MOHIST_OPERATOR_TOKEN`；
2. `~/.mohist/config.jsonc` 中的 `Mohist:OperatorToken`；
3. `MOHIST_OPERATOR_TOKEN_PATH`、`Mohist:OperatorTokenPath` 或默认 `~/.mohist/operator-token` 指向的凭据文件。

环境变量优先于配置文件。默认凭据文件由本机服务首次启动时创建；托管部署可通过配置文件指定共享路径，无需再为 CLI 配置第二份覆盖值。

## Workflow Profile（工作流运行配置）

工作流运行配置 = 模板 + 变量 + 提示词覆盖。挂在 project 下（配置是项目拥有的）。统一到 `mo project workflow profile` 一个资源组，含启用/禁用入口。

```
mo project workflow profile list [--described]      列出 profile（含名称与描述）
mo project workflow profile get                     查看 profile 全貌（默认模板/变量/提示词）
mo project workflow profile set [flags]             复合写入（默认模板/变量/提示词）
mo project workflow profile clear [flags]           复合清除
mo project workflow profile preview <键>            预览渲染后的提示词
mo project workflow profile enable <profile-id>     启用 profile
mo project workflow profile disable <profile-id>    禁用 profile
```

> **路径迁移**：旧 `mo workflow list`（WorkflowProfile）已下沉到 `mo project workflow profile list`。原路径不再可用——profile 归 `mo project workflow`，与 template / config 同层。

`profile list` 支持 `-o table|json` 和 `--described`；同其他项目作用域命令一样，支持 `--project` / `--project-id`。无 project flag 但有 active project 时使用 active project；无可解析 project 时回退到未过滤列表并打印降级 stderr 提示。`--project` 与 `--project-id` 冲突时本地失败、不发请求。完整 flag 见 `mo project workflow profile list --help`。

## Workflow Template（工作流模板）

YAML 定义的模板，project 下管理。

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
mo project status                  当前项目聚合状态
mo project workflow ...            工作流配置（template / profile，见上）
```

## Repository（仓库）

一个 project 可关联多个仓库（如 monorepo 多模块）。仓库是项目作用域内的资源，用 `--project` 限定作用域（对标 `--namespace`），不嵌套在 project 命令下。

```
mo repo list [--project <名>]
mo repo add <名> --git-url <url> [--base-branch <分支>] [--set-default] [--project <名>]
mo repo update <名> [flags] [--project <名>]
mo repo set-default <名> [--project <名>]
mo repo delete <名> [--project <名>]
```

仓库是往项目集合加成员，用 `add`（不是 create）。

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
mo issue rerun <编号> --from-stage <阶段>
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

`compact` 使用当前执行后端的原生压缩并保持底层 Session 身份；`reset` 建立
没有旧上下文的新 Session 并保留会话沿革；`followup` 在执行中进入当前回合，空闲
时开始下一回合。会话身份与来源见 [Agent 与 AgentSession](agents.md)；OpenCode 的
具体行为见 [`mohist/opencode` Action](actions/opencode.md)。

常用示例：

```
mo issue comment add <number> --body "请补充错误处理"
mo issue prereq add <number> <prereq-number>
mo issue prereq remove <number> <prereq-number>
mo issue reject <number> --message <message>
mo issue rerun <number> --from-stage <stage>
```

Issue 的工作流快捷方式（approve/retry/rerun/...）是对应 `mo workflow` 命令的人类便利别名，行为一致。

## Epic（产品目标）

```
mo epic create <title> [options]
mo epic list
mo epic get <epic-id-or-number>
mo epic update <epic-id-or-number> [options]
mo epic link <epic> <issue>
mo epic unlink <epic> <issue>
mo epic start <epic-id-or-number>            开始自动推进
mo epic pause <epic-id-or-number>            暂停自动推进
mo epic resume <epic-id-or-number>
mo epic done <epic-id-or-number>             标记里程碑完成
mo epic close <epic-id-or-number>            放弃里程碑
```

epic 无 delete——里程碑用 done（完成）或 close（放弃）收尾，不删除。

## Mohist Agent（Named Agent）

`mo agent` 管理的是 Project 内有稳定身份的 Mohist Agent。Inline Agent 由 Workflow
task 的 Action Input 定义，不通过这组命令创建或管理。

```
mo agent create [options]
mo agent list
mo agent get <名或id>
mo agent update <名或id> [options]
mo agent archive <名或id>           归档（软删，可恢复）
mo agent session list <agent>
mo agent session get <会话id>
mo agent session transcript <会话id>
mo agent session launch <agent> [--prompt <文本>|--prompt-file <路径>]
mo agent session followup <会话id> [--text <文本>|--text-file <路径>]
mo agent session compact <会话id>
mo agent session reset <会话id>
mo agent session cancel <会话id>
```

`mo issue session ...` 与 `mo agent session ...` 面向同一种 AgentSession。前者按
Workflow 来源查找，后者按 Mohist Agent 来源查找；它们不是两套 Session 模型。

`launch` 是便利入口：它解析指定 Mohist Agent，并同时创建一次 AgentJob 和一段
AgentSession。AgentJob 负责这次执行的成功或失败，命令返回的 Session ID 用于查看
对话和继续 follow-up。`cancel` 只中断当前执行回合，不删除 AgentSession。

## Label（标签）

```
mo label list                       标签目录
mo label add <key> [--description <text>] [--supported-values <v1,v2>]
mo label update <key> [options]
mo label delete <key>                delete 为正名，remove/rm 为别名
```

label 是往项目标签目录加定义，用 `add`（不是 create）。

## Runner（执行器）

```
mo runner start                    启动执行器受管服务
mo runner stop                     停止执行器受管服务
mo runner restart                  重启执行器受管服务
mo runner list [--scope all|global|project]
mo runner get <执行器id>
mo runner status                    在线执行器摘要
mo runner uninstall                 卸载执行器受管服务
```

执行器的安装/启停见「安装与升级」。

## Server（服务端）

```
mo server start                     启动服务端受管服务
mo server stop                      停止服务端受管服务
mo server restart                   重启服务端受管服务
mo server health                    健康检查
mo server info                      服务端系统诊断
mo server status                    服务状态
mo server logs                      服务日志（受管服务的运维日志）
mo server uninstall                 卸载服务端受管服务
```

`mo system logs`（应用日志）与 `mo server logs`（运维日志）内容不同：前者是应用输出，后者是 systemd/计划任务层日志。

## 系统诊断（只读）

```
mo info                             CLI 本地环境与安装来源（受控例外：跨资源只读）
mo system logs                      应用日志
mo otel query <sql> [--db <路径>]   直接查询 OpenTelemetry 数据库（无需服务端）
mo otel status                      OpenTelemetry 采集器状态（需服务端）
mo opencode models                  当前项目可用模型
```

## Notification（通知配置）

```
mo notification setup               通知平台配置向导
```

## 配置（键值）

```
mo config list
mo config get <键>
mo config set <键> <值>
```

`mo config get/set` 是键值范式，与资源 `get` 语义不同但词相同——KV 查询是 CLI 通用惯例（如 git config），保留。

## 技能分发

```
mo skills list
mo skills install
mo skills get <name>
mo skills path <name>
mo skills sync
```

`mo skills` 的输出格式统一走 `-o`，不用 `--json` 布尔开关。

> 工作树 skill-data 同步到托管缓存（`mo skills sync`）：在 worktree 内的 `skill-data/` 目录修改后，必须跑一次 `mo skills sync`，改动才会出现在新启动的 agent session 上下文中。`mo skills sync` 只在「工作树 skill-data 同步到托管缓存」的场景下需要——已经装好且不需要调整的 skill 不必每次都跑。

## 安装与升级（动词根集中）

装机与升级只走动词根 `mo install` / `mo update`。

```
mo install server                   安装服务端为受管服务
mo install runner                   安装执行器为受管服务

mo update                           升级全部（CLI + 服务端 + 执行器）
mo update cli                       仅升级 mo CLI
mo update server                    仅升级服务端
mo update runner                    仅升级执行器
```

## 实装差距

`mo agent session compact/reset` 尚未交付；当前只有 Workflow 来源的 Session 提供
对应 CLI 入口。两种来源会随统一 AgentSession 模型和 `mohist/opencode` 替换一起
对齐。其它命令面随各命令组改进 issue 进一步对齐到本文。

## 典型工作流脚本

```bash
# 批量启动 backlog
for n in 42 43 44 45 46; do mo issue start $n; done

# 重试所有 blocked 的 issue
mo issue list --output json | jq '.[] | select(.health=="blocked") | .number' | \
  while read n; do mo issue retry $n; done

# 直接控制一个工作流执行（不通过 issue 号）
mo workflow get wr_abc123 -o yaml
mo workflow approve wr_abc123
```

## 命令找不到？

- 看完整命令树：`mo --help`
- 看子命令选项：`mo <命令> --help`
- 本文是 spec，不是实装清单——某命令在本文出现但 `mo --help` 没有，说明该命令面改进尚未落地，查对应 issue。

## 退出码

| Code | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 一般错误（参数错、API 返回错误等） |
| 2 | 命令解析失败 |

---

对应源码：`packages/cli/`。
