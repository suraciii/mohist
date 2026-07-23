# CLI 参考

`mo` 是 Mohist 面向人和 Agent 的命令行。它是一门小而稳定的操作语言：命令负责表达意图，帮助负责给出当前版本的精确调用方式，Skill 负责补充只有 Mohist 才有的判断规则。

本文定义目标产品形态。当前版本尚未对齐的部分集中列在文末；在迁移完成前，以本机 `mo --help` 为可执行事实。

## 产品承诺

- **可预测**：看到资源和动作，就能推导命令；同一能力只有一个规范路径。
- **可发现**：从 `mo --help` 进入命令组，再进入叶子帮助，不需要先读完整手册。
- **上下文克制**：每层只提供完成当前决策所需的信息，不重复下一层已经能回答的内容。
- **对自动化稳定**：非交互调用不会等待输入；结构化输出只返回请求的字段；失败总是非零退出。
- **人机同面**：Agent 和人使用同一套命令、帮助与错误信息，不维护第二套 Agent 专用命令面。

Agent 已经会读命令和推理。`mo` 不教它通用 shell 知识，只告诉它 Mohist 的资源、状态约束、相近动作的区别，以及下一条准确命令。

## 使用方式

一次操作通常只需要三层信息：

```bash
mo --help
mo run --help
mo run retry --help
```

| 层级 | 回答的问题 | 内容 |
|---|---|---|
| 根帮助 | 有哪些能力？ | 按任务分组的命令索引、每组一句说明、少量起步示例 |
| 命令组帮助 | 该选哪个动作？ | 资源边界、动作列表、最容易混淆的相邻资源 |
| 叶子帮助 | 这一条怎么准确执行？ | 结果、用法、参数、状态前提、JSON 字段和两三个示例 |
| `mo help <topic>` | 多个命令共用什么规则？ | `output`、`environment`、`exit-codes` 等横切约定 |
| Mohist Skill | 当前场景该怎么推进？ | 首次读取、恢复决策、场景 Skill 路由和少量硬规则 |

帮助不展示服务内部名称、通信路径、源码位置、历史 issue 或迁移别名。它描述当前命令的产品行为。

## 命令语言

命令只有两种形状：

```text
resource-command = mo <area> [<subarea>] <action> [target] [flags]
task-command     = mo <task> [target] [flags]
```

- `area` 是用户正在操作的产品对象或任务区域，例如 `issue`、`run`、`session`。
- `subarea` 只在对象由 area 拥有或离开 area 就失去明确含义时出现，例如 `issue comment`、`issue template`、`routing rule`。
- `action` 是稳定的英文动词，例如 `list`、`view`、`retry`。
- `task` 是无需人为包装成资源的直接任务，目前只有 `help`、`install`、`update`、`info`。
- `target` 优先使用最短的稳定身份；项目作用域通过 `--project` 表达。
- subarea 最多一层。可独立寻址且经常直接操作的资源使用顶层命令，例如 `session`。
- 一项能力只有一个规范路径。筛选条件和便捷寻址使用 flag，不复制一组同义命令。
- 根层不强求语法上的“全是名词”。`install`、`update`、`info` 直接表达任务，比人为包装进抽象资源更清楚。

目标命令面只保留一组规范动词：读取资源使用 `view`，不再并列 `show` 或 `get`；修改资源
使用 `edit`，不再并列 `update`；恢复软删除资源使用 `restore`，不再并列 `unarchive`。
`get` / `set` / `unset` 只用于下文明确声明的键值行为；资源不需要为了对称同时提供三者。
用户自己的 shell alias 不属于 `mo` 契约。

### 动词词表

| 意图 | 规范动词 | 规则 |
|---|---|---|
| 列出资源 | `list` | 返回集合 |
| 查看一个资源 | `view` | 返回单个资源的当前状态 |
| 创建独立资源 | `create` | 创建有稳定身份的资源 |
| 修改资源 | `edit` | 修改现有资源属性 |
| 永久删除 | `delete` | 资源不再存在 |
| 加入或移出集合 | `add` / `remove` | 不删除被关联对象本身 |
| 软删除与恢复 | `archive` / `restore` | 保留身份和历史 |
| 键值配置 | `get` / `set` / `unset` | 只提供该资源已经定义的键值行为，不补齐对称命令 |
| 领域动作 | `start`、`approve`、`retry` 等 | 直接使用 Mohist 的状态变化语言 |

`retry`、`rerun`、`pause`、`stop` 不是同义词：

- `retry` 重试当前失败的 task 或 check，并为这次人工重试恢复完整的自动恢复预算。
- `rerun` 使整条 Run 从头重新执行；`--from-stage <stage>` 只使该 Stage 及之后的结果失效并重新执行。
- `pause` 中断当前推进但保留恢复入口；`resume` 继续同一条 Run。
- `stop` 使 Run 永久终止，不能再 `resume`。

## 命令地图

根帮助按用户任务分组，而不是输出一条没有层次的长列表。

| 分组 | 命令组 | 管理的对象 |
|---|---|---|
| Work | `project`、`repo`、`issue`、`epic`、`label` | 项目空间、工作项与组织关系 |
| Automation | `workflow`、`run`、`agent`、`session`、`activity`、`routing` | 工作流定义与执行、Agent 工作、对话和项目活动 |
| Operations | `runner`、`server`、`service`、`event`、`notification`、`otel` | 执行资源、Server、本机服务、事件投递与可观测性 |
| Tools | `help`、`skill`、`install`、`update`、`info` | 帮助主题、Skill 和安装维护 |

### 核心命令组

命令地图只登记已经有明确产品行为的能力。相邻资源拥有相似动作，不足以成为新增命令的
理由；没有独立场景和语义的对称命令不进入 DSL。

| 命令组 | 规范动作 |
|---|---|
| `project` | `list`、`view`、`create`、`use`、`delete`；`workflow set-default`；`prompt list/view/set/unset/preview`；`variable list/get/set/unset` |
| `repo` | `list`、`view`、`add`、`edit`、`remove`、`set-default` |
| `issue` | `list`、`view`、`create`、`edit`、`start`、`done`、`close`、`reopen`、`archive`、`restore`、`rebase`、`diff`；`comment list/add`；`commit list`；`prereq add/remove`；`template list/view`；`variable list/get/set/unset`；`watch list/add/remove` |
| `epic` | `list`、`view`、`create`、`edit`、`link`、`unlink`、`start`、`pause`、`resume`、`done`、`close`、`reopen` |
| `label` | `list`、`view`、`create`、`edit`、`delete` |
| `workflow` | `list`、`view`、`create`、`edit`、`delete`、`validate`；`view --yaml` 读取原始 Workflow Definition |
| `run` | `list`、`view`、`watch`、`approve`、`reject`、`retry`、`rerun`、`pause`、`resume`、`stop`；`feedback list/view`；`variable list/get/set/unset`，其中 `list/get --effective` 读取合并结果 |
| `agent` | `list`、`view`、`create`、`edit`、`archive`、`restore`、`launch`、`install`；`job list/view`；只读 `model list --runtime` |
| `session` | `list`、`view`、`transcript`、`followup`、`compact`、`reset`、`cancel` |
| `activity` | `list` |
| `routing` | `rule list/view/create/edit/archive/restore/move`；`test` 评估整张路由表 |

### 运维与工具命令组

| 命令组 | 规范动作 |
|---|---|
| `runner` | `list`、`view`、`status` |
| `server` | `status`、`health`、`info`、`logs` |
| `service` | `start`、`stop`、`restart`、`status`、`logs`、`uninstall`，target 为 `server` 或 `runner` |
| `event` | `tail`；`dead-letter list/redeliver` |
| `notification` | `setup` |
| `otel` | `status`、`query` |
| `skill` | `list`、`view`、`install`、`path`、`sync` |
| `help` | 查看 `output`、`environment`、`exit-codes` 等共用规则 |
| `install` | 安装 `server` 或 `runner` |
| `update` | 更新全部组件或一个指定组件 |
| `info` | 查看本机 CLI、安装来源与有效环境 |

每个命令组的叶子帮助才是参数清单。根帮助和本文命令地图不复制所有 flag。

## Issue（工作项）

`mo issue` 管理工作本身：内容、组织关系、目标仓库、Profile 选择，以及 Draft / Done / Closed / Archived 等 Issue 生命周期。`mo issue start <number>` 表达“开始这项工作”，成功后创建并绑定一条 WorkflowRun。

审批、恢复、暂停和终止改变的是 WorkflowRun，因此只放在 `mo run`。Issue 的评论、前置条件、模板、变量、diff 和 commit 仍留在 `mo issue`，因为它们描述或辅助这项工作。

## Workflow Profile

`mo workflow` 管理 Project 范围的 Workflow Profile。WorkflowRun 启动时绑定 Profile ID，
而不是复制 Definition。修改 Issue 的选择或 Project 默认值只影响未来的 Run；编辑已绑定
Profile 会影响活动 Run。完整生效时机以 [Workflow Profile](workflow-profiles.md#选择-profile)
为准，`workflow edit --help` 必须明确提示对活动 Run 的影响。

Profile collection 属于 Workflow；Project 默认选择和 Issue 显式选择是对 Profile 的引用，
不属于 Profile 自身。Project 使用 `mo project workflow set-default <profile>`，Issue 在
`create` 或 `edit` 时使用 `--workflow-profile <profile>`；`issue edit
--inherit-workflow-profile` 清除显式选择并重新继承 Project 默认值，且与
`--workflow-profile` 互斥。`mo workflow` 不复制这些选择动作，也不用可能与合法 Profile ID
冲突的 `default` / `none` sentinel。

`workflow` 与 `run` 的分工沿用 GitHub CLI 中 workflow definition 与 run execution 的心智模型，但使用 Mohist 自己的 WorkflowProfile 和 WorkflowRun 语义。

`mo workflow validate --file <path>` 纯本地校验 Workflow Definition；`--file -` 从 stdin
读取。该命令不解析 Project，也不连接 Server。

## WorkflowRun

`mo run` 查看和控制一次 WorkflowRun。Issue 号可以便捷寻址当前 Run，但不会因此复制 Issue 下的控制命令。

需要一条 Run 的命令接受以下两种目标之一：

```bash
mo run retry wr_abc123
mo run retry --issue 42
```

位置参数直接使用 WorkflowRun ID；`--issue` 解析该 Issue 当前绑定的 Run。两者必须且只能提供一个。Issue 号在 Project 内唯一，因此可同时使用 `--project`。

Project、Issue 和 WorkflowRun 各自拥有一份 Variables。三个 scope 使用相同的
`variable list/get/set/unset` 键值语言；`run variable list/get --effective` 读取 Project →
Issue → Run 合并后的只读结果，并可用 `--stage` 查看指定 Stage。修改任一 scope 后，已经
接受的 attempt 保持原输入，尚未开始的 task、人工 retry 和 recovery continuation 使用开始
时的最新 Variables。

Variables 命令使用与 `${{ vars.* }}` 相同的点分 key path。`--stage <stage>` 把读写限定到
该 scope 的 Stage Variables；不传时操作 workflow-wide Variables。`set` 的位置值按字符串
保存；需要 boolean、number、object 或 array 时改用互斥的 `--value-json <json>`。因此 Agent
无需猜测 shell 文本是否会被自动转换类型：

```bash
mo project variable set agent.model openai/gpt-5
mo issue variable set 42 review.strict --value-json true --stage check
mo issue variable unset 42 review.strict --stage check
mo run variable get --issue 42 agent.model --effective --stage check
```

`list` 和 `get` 读取被选 scope 自己保存的值；只有 Run 提供 `--effective`，因为合并结果是
WorkflowRun 的只读派生事实。`set` 必须且只能接收位置值或 `--value-json` 之一。

## Agent、AgentJob 与 Session

`agent` 是 Project 内有稳定身份的 Mohist Agent。AgentJob 是该 Agent 的一次工作，回答
执行是否完成及结果是什么；AgentSession 是可独立寻址的对话，回答发生了哪些消息、上下文
和用量。CLI 不用 Session 状态代替 AgentJob 结果。

- `mo agent launch <agent>` 创建 AgentJob 与 AgentSession，并返回 Job ID 和 Session ID。
- `mo agent install <name>` 安装内置 Agent 预设（如 `supervisor`：监管 Agent 与审批、
  失败两条路由规则），幂等且不覆盖已有内容；产物是普通 Agent 与 RoutingRule。
- `mo agent job list <agent>` 与 `mo agent job view <job-id>` 读取工作状态和结果。
- `mo agent model list --runtime <runtime>` 读取 Agent 与 Issue 配置时可选择的模型；Runtime
  是配置维度，不是独立命令资源。
- `mo session list --agent <agent>` 查看该 Agent 发起的 Session。
- `mo session list --issue <number>` 查看该 Issue 的 Workflow 产生的 Session。
- `mo session list --run <run-id>` 查看该 Run 的 Session。
- 后续读取、follow-up、compact、reset 和 cancel 都使用稳定的 Session ID。

来源只是筛选和便捷查找条件，不创造 `mo issue session` 与 `mo agent session` 两套重复能力。
`session cancel` 请求中断当前 Runtime 执行；它不表示取消或重写 AgentJob 生命周期。

## Activity、Event 与本机 Service

`activity` 是 Project 范围的只读活动记录，用于回答 Issue、WorkflowRun 和 AgentSession 最近
发生了什么。`event tail` 是从订阅建立后开始的实时 Event 信封流，`event dead-letter` 是
投递恢复操作；三者不共享读取语义，因此不合并为一个带 mode 或 source flag 的命令。

`runner` 只表示 Server 已注册的执行资源及其 presence、capacity 和状态。`server` 只表示
当前连接的 Mohist Server 应用。对本机受管进程的启动、停止和日志读取统一使用
`mo service <action> <server|runner>`。因此 `server logs` 返回应用日志，
`service logs server` 返回本机服务管理器日志，不用 `--source` 在两种行为间切换。

CLI 不提供泛化的根级 `config`。Project Variables、Prompt、Agent 配置和其它产品设置由各自
资源管理；本机安装或服务设置只有出现明确产品场景时才增加类型化命令，不暴露任意 key/value
透传入口。

## Project 作用域

Project 范围内的命令遵循同一套解析规则：

1. 显式传入 `--project <name-or-id>` 时使用该 Project。
2. 否则使用当前目录或本机配置选中的 Project。
3. 无法唯一解析时直接失败，并提示如何传入 `--project` 或选择当前 Project。

命令面只有 `--project`，不再并列 `--project-id`。Project 名称和 ID 都由同一个参数解析。

## 输入与交互

- 短文本使用 `--body`、`--message` 或该命令明确声明的参数。
- 长文本使用与短文本同名的 file flag，例如 `--body-file`、`--prompt-file`、`--text-file`；传 `-` 表示从 stdin 读取。
- Workflow Definition 等完整文档使用 `--file <path>`；传 `-` 表示从 stdin 读取。
- 在 TTY 中，少数安装、setup 和 create 命令可以在缺少可选输入时询问。
- 在非 TTY 中，任何命令都不询问；缺少必填输入时立即失败并给出可执行提示。
- `MOHIST_PROMPT_DISABLED=1` 在任何环境中关闭询问，便于 Agent、脚本和 CI 获得确定行为。
- 永久删除或不可恢复的控制动作在交互环境中确认；自动化通过叶子帮助声明的 `--yes` 显式确认。

不提供“一律支持”的 `--dry-run`。只有能给出真实、完整预览的命令才声明预览能力。

## 输出

默认输出服务于人类阅读：列表是紧凑表格，单个资源是精简详情，成功的状态修改是一行结果。

返回资源的命令支持字段选择：

```bash
mo issue list --json number,title,status
mo run view --issue 42 --json id,status,currentStage
```

- `--json <fields>` 只输出请求的字段。字段顺序不影响语义。
- 不提供通用 `-o` / `--output`；命令只有默认人类视图和显式字段选择两条常规输出路径。
- 单资源输出一个 JSON object；集合输出一个 JSON array。不增加 `{ ok, data, error }` 包装。
- 单独传 `--json` 时列出该命令可选的字段并退出，不要求 Agent 猜字段名。
- JSON 字段是命令契约。叶子帮助列出当前版本支持的字段。
- 连续事件与日志使用一行一个 JSON object 的 NDJSON；不会把无限流包装成数组。
- 正常结果只写 stdout。错误、提示、确认和进度只写 stderr。
- 人类输出允许改善排版；脚本和 Agent 只依赖 JSON 或 NDJSON。

初始命令面不内置 `--jq`、`--template` 或通用 YAML renderer。Agent 可以请求最小 JSON 字段，再使用现有 shell 工具处理；只有反复出现且无法通过字段选择解决的需求，才扩展 CLI。

`mo workflow view <profile> --yaml` 是明确的资源专属视图：Workflow Definition 本身就是 YAML 工件。`--yaml` 与 `--json` 互斥，也不表示其它资源支持 YAML 输出。

## 错误与退出

错误首先要让 Agent 直接修正下一次调用，同时也要让人读得懂：

```text
error: issue 42 has no active workflow run [run_not_found]
hint: start it with `mo issue start 42`
```

- 第一行说明失败对象、原因和稳定错误码。
- `hint:` 只在存在明确恢复动作时出现，并给出可执行命令或缺失参数。
- 参数错误同时展示相关 usage，不倾倒整个根帮助。
- 未知 area 或 action 是用法错误：返回 `2`，只展示最近一级的相关 usage；不得回退到根帮助
  后以 `0` 退出。
- 服务返回的领域错误保留其具体原因，不替换成笼统的 “request failed”。
- 默认不输出调用栈或内部通信细节。

退出码保持小而稳定：

| Code | 含义 |
|---|---|
| `0` | 成功 |
| `1` | 操作失败、状态不允许或服务不可用 |
| `2` | 命令或参数用法错误 |
| `130` | 用户中断 |

错误不因 `--json` 改成另一套 envelope；调用方始终通过退出码判断成功，再从 stderr 读取同一份高质量诊断。

## 典型调用

```bash
# 只读取做决策需要的字段
mo issue list --json number,title,status

# 启动 Issue，再通过 Issue 号查看其当前 Run
mo issue start 42
mo run view --issue 42

# 重试失败点，或从 build Stage 重新执行
mo run retry --issue 42
mo run rerun --issue 42 --from-stage build

# 找到会话后读取 transcript
mo session list --issue 42 --json id,name,status
mo session transcript session_abc123

# 从 stdin 提交长内容
mo issue comment add 42 --body-file -

# 调整当前 Run 的变量；后续 attempt 使用新值
mo run variable set --issue 42 agent.model openai/gpt-5
mo run variable get --issue 42 agent.model --effective --stage check

# 区分远程 Runner 资源和本机 Runner 服务
mo runner status
mo service status runner

# 不连接 Server，校验本地 Workflow Definition
mo workflow validate --file workflow.yaml
```

## Skill 的角色

Mohist Skill 是短决策指南，不是第二份 CLI 参考。它只保留这些内容：

- 对已有 Issue，先读哪些当前事实再行动。
- 何时用 `retry`、`rerun`、`pause`、`stop` 或 `reset`。
- 何时转入 explore、create issue、create epic 等场景 Skill。
- 哪些 Mohist 状态约束不能从通用 CLI 常识推导。
- 最后提醒使用当前叶子帮助确认精确 flag，并只请求需要的 JSON 字段。

完整命令表、通用 flag、输出格式和安装细节不在 Skill 中重复。这样 CLI 升级后，Agent 读取的是当前二进制生成的帮助，而不是一份容易过期的副本。

## 实装差距

当前命令面仍有以下主要差距：

- `workflow` 当前主要表示 WorkflowRun；Workflow Profile 位于更深的 Project 子命令。目标是 `workflow` 管 Profile、`run` 管执行。
- Run 控制目前同时出现在 workflow 和 issue 下。目标只保留 `run` 的规范入口，Issue 号作为 `--issue` 选择器。
- AgentSession 目前按 Issue 与 Agent 来源分散在不同路径。目标统一到 `session`。
- 资源读取和修改混用 `show`、`get`、`update` 等词。目标统一为 `view`、`edit`。
- 项目作用域、输出模式和默认输出尚未统一。目标只保留 `--project` 与字段选择式 `--json`。
- 当前根帮助、叶子帮助和 Mohist Skill 含有重复信息及部分内部实现描述。目标按本文的渐进披露边界重写。
- 当前部分未知 area 或 action 会回退到根帮助并以 `0` 退出；目标是返回 `2`，只展示最近
  一级的相关 usage。
- 当前 Agent launch 只返回 Session，CLI 也没有 AgentJob read surface；目标同时暴露 Job
  与 Session 的稳定身份和各自事实。
- 当前 `opencode` 和根级 `config` 是实现或配置容器导向的入口；目标把模型目录放到 Agent
  配置辅助命令，并删除没有明确资源所有者的泛化 config 命令。
- 其它用户指南在迁移期间仍可能展示当前可运行的旧路径；完成命令迁移后再一次性更新示例。

### 已闭合

- `runner` / `server` / `service` 三层职责：`runner` 只表示 Server 已注册的远程执行资源（`list`/`view`/`status`），`server` 只表示当前连接的 Mohist Server 应用（`status`/`health`/`info`/`logs`，其中 `logs` 是应用日志）；本机受管进程统一为 `mo service <verb> server|runner`。`project status` 已迁移到 `server status`；`system logs` 已合并到 `server logs`，`system` 命令组整体退役。

对应源码：`packages/cli/`。
