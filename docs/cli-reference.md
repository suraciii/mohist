# CLI 参考

`mo` 是 Mohist 面向人和 Agent 的命令行。它是一门小而稳定的操作语言：命令负责表达意图，帮助负责给出当前版本的精确调用方式，Skill 负责补充只有 Mohist 才有的判断规则。

本文定义目标产品形态。当前版本尚未对齐的部分集中列在文末；在迁移完成前，以本机 `mo --help` 为可执行事实。

## 产品承诺

- **可预测**：看到资源和动作，就能推导命令；同一能力只有一个规范路径。
- **可发现**：从 `mo --help` 进入命令组，再进入叶子帮助，不需要先读完整手册。
- **上下文克制**：每层只提供完成当前决策所需的信息，不重复下一层已经能回答的内容。
- **对自动化稳定**：非交互调用不会等待输入；结构化输出只返回请求的字段；失败总是非零退出。
- **错误可恢复**：命令语法、参数和字段选择错误返回 2，并在 stderr 给出最近命令的用法；这类错误不会请求 Server。
- **人机同面**：Agent 和人使用同一套命令、帮助与错误信息，不维护第二套 Agent 专用命令面。

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

### Flag 约定

flag 词汇在全命令面唯一，同一个词不表达两种含义：

- 集合规模只有 `--limit`（`service logs --lines` 是日志尾部语意的例外，沿用行业惯例）。
- 资源引用 flag 唯一：仓库一律 `--repo`，Project 一律 `--project`，不并列同义词。
- 互斥关系由参数定义声明，并且必须在叶子帮助中可见。
- 短 flag 是白名单：只保留全局字母唯一、行业惯例明确的 `-l`（`--label`）、`-p`（`--priority`）、`-b`（`--body`）、`-m`（`--message`）、`-y`（`--yes`）、`-f`（`--follow`）、`-n`（`--lines`）、`-v`（`--verbose`），且必须渲染进叶子帮助；白名单之外不新增短 flag。
- Project 的默认引用（默认仓库、默认 Workflow Profile）是 Project 的属性，统一由 `project` area 承载：`project repo set-default`、`project workflow set-default`。

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
| `project` | `list`、`view`、`create`、`use`、`delete`；`workflow set-default`；`workflow prompt get/set/clear/preview`；`repo set-default`；`variable list/get/set/unset` |
| `repo` | `list`、`create`、`edit`、`delete` |
| `issue` | `list`、`view`、`create`、`edit`、`start`、`done`、`close`、`reopen`、`archive`、`restore`、`rebase`、`diff`、`commits`、`logs`、`events`；`comment create`；`prereq add/remove`；`template list/view`；`variable list/get/set/unset`；`watch list/add/remove` |
| `epic` | `list`、`view`、`create`、`edit`、`add`、`remove`、`start`、`pause`、`resume`、`done`、`close`、`reopen` |
| `label` | `list`、`create`、`edit`、`delete` |
| `workflow` | `list`、`view`、`create`、`edit`、`delete`、`validate`；`view --yaml` 读取原始 Workflow Definition |
| `run` | `list`、`view`、`watch`、`approve`、`reject`、`retry`、`rerun`、`pause`、`resume`、`stop`；`view --yaml` 读取 Run 绑定的 Definition 快照；`feedback list/view`；`variable list/get/set/unset`，其中 `list/get --effective` 读取合并结果 |
| `agent` | `list`、`view`、`create`、`edit`、`archive`、`restore`、`launch`、`spawn`、`install`；`job list/view`；只读 `model list --runtime` |
| `session` | `list`、`view`、`tree`、`transcript`、`followup`、`compact`、`reset`、`cancel`、`stop`、`detach` |
| `activity` | `list` |
| `routing` | `rule list/view/create/edit/archive/move`；`test` 评估整张路由表 |

### 运维与工具命令组

| 命令组 | 规范动作 |
|---|---|
| `runner` | `list`、`view`、`status` |
| `server` | `status`、`health`、`info`、`logs` |
| `service` | `start`、`stop`、`restart`、`status`、`logs`、`uninstall`，target 为 `server`、`runner` 或 `slack` |
| `event` | `tail`；`dead-letter list/redeliver` |
| `notification` | `setup` |
| `slack` | `setup`、`configure-manager`、`status`；`list`、`view`、`create`、`configure`、`claim-owner`、`edit`、`rotate-credentials`、`transfer-owner`、`enable`、`disable`、`delete`；`deliveries`、`resend-delivery`、`clear-gap`、`create-child-app`、`reconcile-create`、`reconcile-delete`、`remove-binding`、`permanent-delete` |
| `otel` | `status`、`query <sql>`、`traces`，`query` 经 Server 执行并支持 `--json <fields>` 字段选择 |
| `skill` | `list`、`view`、`install`、`path`、`sync` |
| `help` | 查看 `output`、`environment`、`exit-codes` 等共用规则 |
| `install` | 安装 `server`、`runner` 或 `slack` |
| `update` | 更新全部组件或一个指定组件 |
| `info` | 查看本机 CLI、安装来源与有效环境 |

每个命令组的叶子帮助才是参数清单。根帮助和本文命令地图不复制所有 flag。

## Issue（工作项）

`mo issue` 管理工作本身：内容、组织关系、目标仓库、Profile 选择，以及 Draft / Done / Closed / Archived 等 Issue 生命周期。`mo issue start <number>` 表达“开始这项工作”，成功后创建并绑定一条 WorkflowRun。

`issue create` 与 `issue edit` 使用同一组类型化 flags 设置规划元数据：`--priority`、`--risk low|medium|high`、`--label`、`--repo`、`--parent`、`--workflow-profile`。这些字段是结构化数据，不需要写进正文 frontmatter。

`issue list` 支持 `--stage`、`--priority`、`--label`、`--repo`、`--parent`、`--epic` 过滤；配合 `--json` 的字段选择，一次调用即可完成跨 Issue 的对照排查，不需要逐条 `issue view`。

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

`mo workflow create` 与 `mo workflow edit` 用 `--file <path>` 提供 Workflow Definition，`--file -` 从 stdin 读取。`mo workflow validate --file <path>` 纯本地校验 Workflow Definition；该命令不解析 Project，也不连接 Server。

## WorkflowRun

`mo run` 查看和控制一次 WorkflowRun。Issue 号可以便捷寻址当前 Run，但不会因此复制 Issue 下的控制命令。

需要一条 Run 的命令接受以下两种目标之一：

```bash
mo run retry wr_abc123
mo run retry --issue 42
```

位置参数直接使用 WorkflowRun ID；`--issue` 解析该 Issue 当前绑定的 Run。两者必须且只能提供一个。Issue 号在 Project 内唯一，因此可同时使用 `--project`。

`mo run view --yaml` 读取该 Run 启动时绑定的 Workflow Definition 快照——即这次执行实际使用的定义，而不是 Profile 的当前内容。它与 `workflow view --yaml` 同属资源 source view，与 `--json` 互斥。

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

`agent` 是 Project 内有稳定身份的 Mohist Agent。AgentJob 是该 Agent 一次 launch 的首次
执行，回答这次启动是否完成及结果是什么；AgentSession 是可独立寻址的持续对话，回答发生
了哪些消息、上下文和用量。CLI 不用 Session 状态代替 AgentJob 结果，也不把 Job completed
解释为对话关闭或用户目标已经交付。

- `mo agent launch <agent>` 创建 AgentJob、AgentSession、首条 SessionInput 与首个 AgentTurn，
  并返回四个稳定 ID、transcript URL 和 composite observation URL。命令接受
  `--idempotency-key`；省略时会在请求前打印生成的 key，响应丢失后必须用该 key 重试。
- `mo agent spawn <agent-ref> --parent-session <session-id> --prompt <brief> --idempotency-key <key>`
  从父会话委托一个已声明的 Subagent；父会话与目标必须在同一 Project，Server 在首次接受时
  解析目标并核对父会话的能力声明，`--idempotency-key` 必填，网络失败后用同一个 key 重试。
- `mo agent create/edit` 使用类型化的 `--runtime`、`--model`、`--variant`、`--skills` 和
  `--max-concurrent-runs` 配置 Agent；可用 Subagent 用可重复的 `--allowed-subagent <agent-id>`
  按稳定 Agent ID 声明；头像使用 `--avatar-file`，Instructions 使用互斥的
  `--instructions` 或 `--instructions-file`。CLI 不要求调用方拼 Agent config JSON，
  `--agent-config` 透传入口退役。
  `mo agent view` 显示统一 Readiness、配置缺口与当前执行可用性；并发限制实时约束 launch
  和 follow-up，但不强停已在运行的执行。
- `mo agent install <name>` 安装内置 Agent 预设（如 `supervisor`：监管 Agent 与审批、
  失败两条路由规则），幂等且不覆盖已有内容；产物是普通 Agent 与 RoutingRule。
- `mo agent job list <agent>` 与 `mo agent job view <job-id>` 读取工作状态和结果。
- `mo agent model list --runtime <runtime>` 读取 Agent 与 Issue 配置时可选择的模型；Runtime
  是配置维度，不是独立命令资源。
- `mo session list --agent <agent>` 查看该 Agent 发起的 Session。
- `mo session list --issue <number>` 查看该 Issue 的 Workflow 产生的 Session。
- `mo session list --run <run-id>` 查看该 Run 的 Session。
- 后续读取、follow-up、compact、reset、cancel 和 stop 都使用稳定的 Session ID；cancel 通过
  `--turn-id` 指定目标 Turn。follow-up 返回新的
  Input ID；已经归入当前 Turn 或新 Turn 时同时返回 Turn ID，否则稍后从 Session 读取归属。
- `mo session tree <session-id>` 展示以该会话为根的整棵会话树和每个节点的状态；树很大时
  按页返回，一次连续翻页固定观察第一次查看时的树形。
- `mo session stop <session-id> --idempotency-key <key>` 级联停止该会话当时挂着的整棵子树
  正在进行的工作；`--idempotency-key` 必填，同 key 重试同一操作。会话本身保留，可以之后
  明确继续。
- `mo session detach <session-id>` 把子会话从树上摘下，之后停止父不再影响它。

来源只是筛选和便捷查找条件，不创造 `mo issue session` 与 `mo agent session` 两套重复能力。
`session cancel` 确定性取消一个排队中的 Turn；它不接触 Runtime，只作用于 `--turn-id`
指定的 Turn。

## Slack

`mo slack` 管理 Slack 接入：一个 Mohist Agent 与一个 Slack 工作区中 Bot 身份的绑定，
以及 workspace 级 Mohist App 的安装。

- `mo slack setup` 用显式参数登记工作区级 Mohist App；完整的路径选择、安装授权与操作者认领
  向导尚未实装。`mo slack status` 查看该工作区各接入的整体状态与唯一下一步。
- `mo slack configure-manager --workspace-team <team> [--credentials-file <path>]` 为活动 workspace
  enrollment 提供或轮换 Manager Bot 凭据。无文件时使用隐藏输入；有文件时要求用户专属、受保护且
  非符号链接的文件，文件只包含一个非空的 Bot 凭据字段。命令不接受 token 字面量参数，成功输出
  只确认 workspace 和凭据已 provisioned，不显示凭据。`status` 区分凭据引用已配置与凭据已提供。
- `mo slack create <agent>` 只创建可恢复接入，输出 Slack identity preview、预填创建地址
  与接入 ID；不要求 `mohist-slack` 在线，也不读取凭据。
- `mo slack configure <id>` 使用隐藏输入提交 Slack 凭据，不接受 token literal flag。非交互
  环境增加 `--credentials-file <path>`；缺少时立即失败，不等待输入。接入服务离线时保存后
  返回 Waiting for Slack service。
- `mo slack claim-owner <id>` 只在 identity verification 完成后生成并显示一次 setup
  claim、有效期和 Slack DM 步骤；再次运行立即使旧 claim 失效。
- `mo slack view <id>` 始终返回 setup progress、status 和唯一 next action；命令可以退出，
  安装与认领不依赖原进程存活。
- `mo slack list <agent>` 读取该 Agent 的所有接入；
  `view/configure/claim-owner/edit/rotate-credentials/transfer-owner/enable/disable/delete <id>`
  管理一个接入。`edit --access-policy allowlist` 用可重复的 `--allow-member <slack-member-id>`
  原子替换非 Owner 成员；Owner 转移通过新的单次认领完成。
- `disable` 可恢复并保留 Agent 与全部执行历史；`delete --yes` 删除连接凭据与关系，但不
  删除 Agent、AgentJob 或 AgentSession，也不代替用户从 Slack 卸载 App。
- 受管子 App 与投递诊断：`deliveries`、`resend-delivery`、`clear-gap`、
  `create-child-app`、`reconcile-create`、`reconcile-delete`、`remove-binding`、
  `permanent-delete`（高危动作要求显式确认，不出现在 Mohist App 对话中）。

接入只拥有外部身份、权限和连接状态；Agent 配置仍由 `agent edit` 修改。日常挂载与调整的
主路径是在 Slack 中与 Mohist App 对话；CLI 与 Web 操作同一条接入记录。完整产品语义见
[Slack](slack.md)。

## Activity、Event 与本机 Service

`activity` 是 Project 范围的只读活动记录，用于回答 Issue、WorkflowRun 和 AgentSession 最近
发生了什么。`event tail` 是从订阅建立后开始的实时 Event 信封流，`event dead-letter` 是
投递恢复操作；三者不共享读取语义，因此不合并为一个带 mode 或 source flag 的命令。

`runner` 只表示 Server 已注册的执行资源及其 presence、capacity 和状态。`server` 只表示
当前连接的 Mohist Server 应用。对本机受管进程的启动、停止和日志读取统一使用
`mo service <action> <server|runner|slack>`；`slack` 是可选的 `mohist-slack` 接入服务，
不是 `mo slack` 管理的接入资源。因此 `server logs` 返回应用日志，
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
- 长文本与结构化值使用与短文本同名的 file flag，例如 `--body-file`、`--prompt-file`、`--text-file`、`--stage-models-file`；传 `-` 表示从 stdin 读取。
- Workflow Definition 等完整文档使用 `--file <path>`；传 `-` 表示从 stdin 读取。
- 文件与 stdin 只有 `--<name>-file` 和 `--file` 两条通道；不接受 `@<file>` 写法。
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
- 单独传 `--json` 时列出该命令可选的字段并退出，不要求 Agent 猜字段名；字段发现优先于其它参数校验，不要求先补齐必填项。
- JSON 字段是命令契约。叶子帮助列出当前版本支持的字段。
- 每个资源只有一份字段目录：同一资源的 `list`、`view` 与返回该资源的 mutation 共享同一组字段名与语义；字段目录覆盖该资源的全部用户可见字段（例如 Issue 含 `number`、`title`、`status`、`stage`、`priority`、`risk`、`labels`、`repository`、`prereq`、`epic`、`workflowRunId`、`createdAt`、`updatedAt`），不放置该资源并不拥有的占位字段。
- 返回资源的 mutation 同样接受 `--json`，字段与对应 `view` 相同。
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
mo issue comment create 42 --body-file -

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

### spec 在前，实现追赶

- `agent restore`；`agent create/edit` 类型化 `--runtime/--model/--variant/--avatar-file` 与
  Readiness 输出；`--agent-config` 透传入口退役。
- `install/update/service ... slack` 与 `mohist-slack` 受管服务。
- `mo slack setup` 的完整托管或本机安装向导，以及真实 Slack 子 App 的创建、授权和审批流程。

### 已闭合

- Issue 字段目录补齐：`issue view/list --json` 增加 `risk`、`prereq`、`epic`、`createdAt`、`updatedAt`；
  `issue list` 增加 `--epic` 过滤；`issue edit` 增加 `--risk`（`create` 已有）。依赖 Issue 读取
  包含 Epic 归属与前置条件。
- Epic 字段目录真实化，并增加 `progress`（含 `nextIssueNumber`、`nextIssueReason`）。
- 输入通道统一：`workflow create/edit --file` 替换 `--yaml <source|@file>`；`--stage-models-file`
  等 file flag 补齐；`@<file>` 写法整体退役。
- `routing test --last` 改为 `--limit`；`agent launch --repository` 改为 `--repo`。
- 短 flag 白名单化：保留 `-l/-p/-b/-m/-y/-f/-n/-v` 并渲染进叶子帮助；删除白名单之外的
  `-s/-d/-u/-i`，以及 `-b` 在 `--base-branch` 上的复用（`-b` 只属于 `--body`）；
  Skill 示例中的 `-d`/`-p` 引用随之一并更新。
- 互斥关系由参数定义声明并写进叶子帮助（`--all/--archived`、`--before/--after`、
  `--feedback/--latest`、`--yaml/--json`、`--inherit-workflow-profile/--workflow-profile` 等）。
- 裸 `--json` 字段发现优先于其它参数校验（当前 `session list --json` 先报筛选缺失）。
- 根级 `slack` 命令组及其 setup/status 已交付；接入动作只保留 `mo slack` 这一命令面。
- Agent launch/follow-up 以稳定的 SessionInput 与 AgentTurn 身份返回；Slack 输入进一步保留
  Server 生成的回复锚点和协作上下文。
- 返回资源的 mutation 一律接受 `--json`（当前 `agent create/edit/archive` 等缺；`issue rebase`
  返回排队应答而非资源，不在此列）。
- `project repo set-default`（自 `repo set-default` 迁移）。
- `project workflow prompt` 等命令的 JSON FIELDS 替换为真实字段目录，移除兜底默认字段集。
- `issue workflow status/timeline` 已随 issue #498 退役，Run 读取收敛到 `run`。

- `runner` / `server` / `service` 三层职责：`runner` 只表示 Server 已注册的远程执行资源（`list`/`view`/`status`），`server` 只表示当前连接的 Mohist Server 应用（`status`/`health`/`info`/`logs`，其中 `logs` 是应用日志）；已实装的本机受管进程统一为 `mo service <verb> server|runner`，目标命令面再增加可选 `slack` service。`project status` 已迁移到 `server status`；`system logs` 已合并到 `server logs`，`system` 命令组整体退役。
- Subagent 会话树命令面已交付：`agent spawn`（`--parent-session` + 必填 `--idempotency-key`，Server 按能力声明授权）、`session tree`（`--limit`/`--continuation` 分页）、`session stop`（级联停止，必填 `--idempotency-key`）、`session detach`。`session stop` 不再以 `--turn-id` 停止单个 Turn，Turn 级控制只保留 `session cancel --turn-id`。
- `agent create/edit` 增加 `--allowed-subagent <agent-id>` 能力声明，按稳定 Agent ID 保存，不按名字或 ref。
- Agent launch 同时暴露 Job 与 Session 的稳定身份：`mo agent launch <agent>` 直接挂在 `agent` 下（不再经过 `agent session launch`），打印 Job 与 Session 的稳定 ID 及各自读取入口；Job ID 可被 `agent job view` 直接使用，无需换算。
- AgentSession 对话统一到顶层 `mo session`：`mo session` 直接挂在根下，`view` / `transcript` / `followup` / `compact` / `reset` / `cancel` 都以稳定 Session ID 寻址，不论该 Session 来自 Agent launch 还是 Workflow run；`list` 通过 `--agent <agent>` / `--issue <number>` / `--run <run-id>` 之一筛选，不创建 `mo issue session` 与 `mo agent session` 两套重复能力。`mo issue sessions <number>` 与 `mo agent session …` 已退役，运行返回 command-not-found。
- `workflow` / `run` 分工：`workflow` 管理 Project 范围的 Workflow Profile，`run` 管理 WorkflowRun 的执行与控制；两组帮助互相链接，Run 控制动词只保留 `run` 入口，Issue 号作为 `--issue` 选择器。
- 资源读取与修改动词统一为 `view` / `edit`；`show` / `get` / `update` 已退役，旧词解析失败并以 `2` 退出。
- 根级 `opencode` 与泛化 `config` 入口已移除；模型目录通过 `agent model list --runtime` 提供。
- 未知 area 或 action 返回用法错误 `2`，只展示最近一级的相关 usage，不回退根帮助并成功退出。
- Project 作用域与输出模式统一：Project-scoped 命令共享 `--project <name-or-id>`，资源结果共享字段选择式 `--json`。
- 根帮助分组与命令地图一致：Work / Automation / Operations / Tools 四组归属同本文登记。
- Project Prompt 已实装于 `project workflow prompt get/set/clear/preview`。
- `run view --yaml` 已实装并登记进 spec：读取 Run 绑定的 Definition 快照，与 `--json` 互斥。

对应源码：`packages/cli/`。
