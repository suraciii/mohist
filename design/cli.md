# CLI Design

`mo` 是 Mohist 面向人和 Agent 的操作语言。它把领域意图编码成稳定命令，把执行所需的最小准确上下文放在当前层级。

本文定义命令语言、上下文分层和实现约束。目标命令面与用户可见语义在 [`docs/cli-reference.md`](../docs/cli-reference.md)。

## Goals

- Agent 只凭 Mohist Skill 与当前版本的 `mo --help` 就能发现并准确执行常见操作。
- 人和 Agent 使用同一命令面、帮助、输出和错误，不维护平行协议。
- 命令能从领域对象和动作推导；一项能力只有一个规范路径。
- 默认输出适合人阅读，结构化输出允许调用方只取决策所需字段。
- 非交互行为确定；参数、状态和失败都能在一次反馈中给出下一步。
- 帮助与 Skill 保持短小，每句话都改变选择、输入或恢复动作。

`mo` 不承担以下职责：

- 不在 Skill 或帮助中教授通用 shell、JSON、Git 或 Agent 推理方法。
- 不把完整产品手册、内部接口或实现历史塞进 `--help`。
- 不为了语法整齐发明没有产品意义的资源。
- 不成为任意服务接口的通用透传客户端。
- 不提供一套与人类命令分离的 “Agent mode”。

## Model

CLI 导航以用户意图为主，同时尊重领域所有权。顶层命令不机械映射代码模块或聚合；一个对象可独立寻址或用户会直接以它为操作起点时，才值得成为顶层 area。

| Area | 对应产品 / 领域概念 | 作用域与边界 |
|---|---|---|
| `project` | Project | Project Space 根入口；拥有 Prompt 与 Project Variable |
| `repo` | Repository | Project 范围的命名执行资源；Issue 只引用它 |
| `issue` | Issue | 工作项及其自身生命周期；`start` 负责开始工作 |
| `epic` | Epic | 与 Issue 同一限界上下文，但有独立身份和生命周期 |
| `workflow` | WorkflowProfile | Project 范围的 Workflow 定义入口，不表示一次执行 |
| `run` | WorkflowRun | 一次 Workflow 执行及其审批、恢复和终止动作 |
| `agent` | Mohist Agent | Project 范围的可复用 Named Agent |
| `session` | AgentSession | 稳定的逻辑会话，与来源无关地统一寻址 |
| `runner` | Runner | 执行资源及受管服务操作 |

`run` 是 WorkflowRun 的命令行短名。`workflow` 是 WorkflowProfile 的导航名；group help 的首句必须写明它管理 Workflow Profile，不能让用户把它理解成 WorkflowRun。CLI 短名不引入新的领域概念，也不改变 [`domain-analysis.md`](domain-analysis.md) 的所有权。

`workflow edit` 修改 Profile 资源，而不是只为未来 Run 准备配置。Profile ID 绑定、
Definition 与 Variables 的生效时机由产品 [Workflow Profile spec](../docs/workflow-profiles.md#选择-profile)
统一定义；CLI 不复制另一套生命周期规则。`workflow edit --help` 必须明确该操作可能影响
活动 Run，并链接 `run --help` 以区分 Profile 与执行。

### Canonical ownership

动作只放在拥有该状态变化的 area：

- `issue start` 开始一项工作并取得当前 WorkflowRun；它是 Issue 动作。
- `run approve/reject/retry/rerun/pause/resume/stop` 改变 WorkflowRun；不在 `issue` 下复制。
- `agent launch` 启动 Mohist Agent 工作并返回 AgentSession；它是 Agent 动作。
- `session transcript/followup/compact/reset/cancel` 改变或读取 AgentSession；不按 Issue 来源和 Agent 来源复制两套路径。
- `--issue`、`--run`、`--agent` 是解析或筛选条件，不转移动作所有权。

subarea 用于没有独立操作入口的从属资源（`issue comment`、`project prompt`），或只服务于一个 area 的窄目录（`issue template`、`routing rule`）。AgentSession 有稳定 ID、独立生命周期且经常直接操作，因此必须是顶层 `session`。

## Command language

规范语法只有两种：

```text
resource-command = mo <area> [<subarea>] <action> [target] [flags]
task-command     = mo <task> [target] [flags]
```

resource command 表达资源及其状态变化。subarea 最多一层，用于 area 拥有的对象，或离开 area 就失去明确含义的窄目录，例如 `issue comment`、`issue template`、`routing rule`。task command 只用于 `help`、`install`、`update`、`info` 这类无需人为包装成资源的直接任务。这是一门受约束的命令 DSL，而不是要求所有句子满足同一种语法外观。设计顺序是：先确定领域意图和唯一入口，再选择最短的惯用命令词。

### Naming

- area 使用短、稳定、通常为单数的英文词：`repo`、`run`、`skill`，不为了对应类型名写成 `repository`、`workflow-run`、`skills`。
- 资源读取统一为 `list` / `view`，资源修改统一为 `create` / `edit` / `delete`。
- `add` / `remove` 只表达集合或关系变化；`archive` / `restore` 只表达可恢复的软删除。
- `get` / `set` / `unset` 只用于明确的键值行为；资源不需要为了对称同时提供三者。
- 状态变化使用领域动词。`retry`、`rerun`、`pause`、`stop` 的语义不能由 CRUD 词替代。
- 同一 action 在所有 area 保持同一动作类别。不同语义不能只因实现复用而使用同一个词。
- 命名对称不是新增命令的理由。没有独立产品行为的 action 不进入命令树。
- flag 使用完整、稳定的 kebab-case 名称。高频且行业惯例明确时才增加短 flag。

### One capability, one path

- 规范命令不提供内建同义词或迁移 alias。
- 同一资源的寻址方式是 target 或 selector flag 的变体，不复制 action。
- 动作变体使用 flag，例如 `run rerun --from-stage`，不增加 `rerun-from-stage`。
- Project 名称与 ID 都由 `--project` 解析，不增加 `--project-id`。
- 互斥输入只有一个表达通道：长文本统一使用对应的 `--<name>-file -` 读取 stdin，不增加 `--stdin` 布尔开关。
- 只有共享同一语义、校验和结果的变体才能合并为 flag。行为不同就保留不同 action。

### Reference baseline

`gh` 是交互设计参考，不是兼容目标。`mo` 借鉴四项已经被验证的形态：分层的 [root / group / leaf help](https://cli.github.com/manual/gh)、[workflow](https://cli.github.com/manual/gh_workflow) 与 [run](https://cli.github.com/manual/gh_run) 分工、[字段选择式 JSON](https://cli.github.com/manual/gh_help_formatting)，以及轻量的 [Skill 入口](https://cli.github.com/manual/gh_skill)。

`mo` 不照搬 `gh api`、内建 `--jq`、template renderer 或 alias 体系。它们解决的是 GitHub 的范围和兼容需求；Mohist 只有在出现自己的重复用例后才增加对应能力。

### Main trade-offs

| 方案 | 结果 | 决定 |
|---|---|---|
| 根层只允许领域名词，并把所有任务包装成资源 | 语法表面整齐，但会产生 `component install`、`system info` 等人为层级 | 不采用；保留清楚的 `install`、`update`、`info` 任务入口 |
| Skill 维护完整命令表，或增加机器可读 command catalog | 初次读取看似完整，但与运行版本重复、容易过期，并消耗大量上下文 | 不采用；Skill 做决策，运行时 help 做语法发现 |
| 所有结果使用通用 `{ok,data,error}` envelope | 统一了传输形状，却增加无信息字段并迫使人类输出绕过同一模型 | 不采用；成功输出原始资源，失败使用退出码和 stderr |
| 默认倾倒完整 JSON，由 Agent 自行过滤 | 实现简单，但把无关字段和 token 成本推给每次调用 | 不采用；默认人类视图，自动化显式选择 JSON 字段 |

## Context architecture

Agent 获得上下文的顺序是：

```text
Mohist Skill -> root/group help -> leaf help -> result or actionable error
```

每层只承担一个决策：

| 来源 | 唯一职责 | 不应包含 |
|---|---|---|
| Mohist Skill | 判断场景、首次读取、危险动作与相近恢复动作 | 完整命令树、通用 flags、实现启动命令 |
| Root help | 建立产品能力地图 | 叶子 flags、完整状态语义 |
| Group help | 解释对象边界并帮助选择 action | 其它 group 的参考手册 |
| Leaf help | 让一条调用无需猜测即可执行 | 源码、接口路径、历史兼容说明 |
| Result | 返回本次操作需要的事实 | 无关资源的完整快照 |
| Error | 说明本次失败及确定的下一步 | 内部调用链、模糊的通用建议 |

### Context quality

所有帮助、Skill 和错误文案按六个维度审查：

| 维度 | 判定标准 |
|---|---|
| Authoritative | 来自当前命令模型、输出字段定义或领域状态，不凭文案副本猜测 |
| Relevant | 只回答当前层需要做的选择 |
| Sufficient | 执行所需参数、前提、危险后果和恢复动作没有缺口 |
| Concise | 删除不改变下一步行为的句子，不重复其它权威层 |
| Executable | 示例可被当前命令树解析，hint 可以直接运行或补齐 |
| Current | help 与二进制同版本；Skill 不冻结易变化的 flag 清单 |

一个实用门槛是：如果删掉一句话不会改变 Agent 选择哪个命令、传什么参数或如何恢复，就不应把它放进默认上下文。

## Syntax authority

[`docs/cli-reference.md`](../docs/cli-reference.md) 是目标产品语义和命令面的 spec。实装后，C# 的 `System.CommandLine` 命令树是该版本唯一的可执行语法权威：

- `mo --help`、group help 和 leaf help 都从命令树生成。
- 参数必填、互斥、默认值和合法值由同一参数定义校验。
- 每个资源结果的 JSON 字段集合由一份字段定义同时驱动选择、序列化和 leaf help。
- Skill 中出现的命令示例必须通过同一命令树的解析测试。
- 不再增加独立的 `mo command list/get` catalog；它会复制命令树并扩大同步面。

spec 先于实现时，差距只写在产品文档的 Status。迁移完成的同一变更必须同时更新命令树、生成帮助、示例测试和差距说明，不能长期保留两套“权威”。

## Help contract

所有 `--help` 都是纯本地、快速、无副作用的操作，成功退出且不依赖 Server。

### Root help

固定顺序：

1. 一句产品说明。
2. `USAGE`。
3. 按 Work、Automation、Operations、Tools 分组的命令，每项一句结果说明。
4. 两到三个覆盖发现、读取和恢复的示例。
5. `mo help <topic>` 与文档入口。

Root help 是索引，不显示所有共享 flag，也不展开子命令。

### Group help

固定顺序：

1. 一句说明该 area 是什么、作用域是什么。
2. `USAGE`。
3. action 列表，每项一句结果说明。
4. 只有确实容易混淆时才增加 `SEE ALSO`。

例如 `workflow --help` 必须指出它管理 Workflow Profile，并链接 `run --help`；`run --help` 必须指出它管理 WorkflowRun，并说明可用 Run ID 或 `--issue` 寻址。

### Leaf help

固定顺序：

1. 一句准确结果，使用产品和领域语言。
2. 一个或多个合法 `USAGE` 形式。
3. arguments 与 options；说明 required、默认值、互斥关系和合法值。
4. 仅在影响选择时说明状态前提、不可恢复后果或相近动作区别。
5. 对资源结果列出 `JSON FIELDS`。
6. 最多三个可独立执行的 `EXAMPLES`。
7. 必要的 `SEE ALSO`。

Leaf help 禁止出现：

- API route、HTTP method、DTO、grain、handler、class 或源码路径。
- issue 编号、迁移阶段、旧命令或“等价于旧路径”。
- 通用 shell 教程和 Agent 操作常识。
- 没有行为约束的宣传性描述。

被三个以上命令组共享且无法由参数定义自解释的内容，移入 `mo help output`、`mo help environment` 或 `mo help exit-codes`。只被一两个命令使用的规则留在 leaf help，避免过早抽象 help topic。

## Skill contract

Mohist Skill 使用渐进披露：入口 Skill 只保留高价值决策，场景细节按需加载 sibling Skill。

入口 Skill 的正文结构固定为：

1. Scope：何时使用 Mohist Skill。
2. First read：收到已有 Issue / Run 时先取哪些当前事实。
3. Scenario routing：何时加载 explore、create issue、create epic 等 Skill。
4. Hard decisions：`retry/rerun`、`pause/stop`、`compact/reset` 等无法从通用 CLI 推导的区别。
5. CLI handoff：用 leaf help 确认精确 flags，并用 `--json` 只请求需要的字段。

入口 Skill 不复制：

- 完整 Issue / Epic 生命周期表。
- 所有 read-only helper 和 common flags。
- Server、Runner、测试或源码启动方式。
- 已移除实现与兼容历史。
- leaf help 已经准确表达的参数说明。

Skill frontmatter description 只负责触发判断。正文中的命令示例必须少量、规范且可解析。复杂场景放入 sibling Skill 或 reference；不存在真实分支时不增加文件层级。

## Input and scope

### Project resolution

所有 Project-scoped 命令复用一个 inherited `--project <name-or-id>` option 和同一 resolver：

```text
explicit --project
  else project resolved from cwd
  else configured current project
  else actionable error
```

解析结果必须唯一。名称、ID 和当前项目只是同一 ProjectRef 的输入形式，不能形成不同 handler 路径。

### Interactivity

- TTY 只影响提问、颜色和人类排版，不改变命令语义。
- 非 TTY 永不 prompt；输入不足立即返回 usage error。
- `MOHIST_PROMPT_DISABLED=1` 强制使用非交互行为。
- body 与 body-file、target 与 selector 等互斥输入在本地拒绝，不能静默选一个覆盖另一个。
- `--<name>-file -` 和文档输入的 `--file -` 是 stdin 的唯一长文本入口。
- 不可恢复动作的非交互调用要求显式 `--yes`；TTY confirmation 也必须在 stderr。
- help、list、view 和本地校验绝不触发 setup prompt。

## Output contract

命令先产生语义结果，再选择 renderer。TTY 判断和输出格式不能改变请求、资源选择或状态变化。

### Human output

- 默认 list 使用紧凑 table，view 使用稳定标签的 summary，mutation 使用一行 outcome。
- table 只保留扫描和下一步判断需要的列。
- 人类排版不是脚本契约；非 TTY 不自动切换成另一种语义输出。
- 颜色遵循终端能力与 `NO_COLOR`，stderr 重定向后不保留控制字符。

### JSON output

- 返回资源的命令接受 `--json <field,...>`。
- field 在执行远程操作前本地校验；未知 field 返回合法字段和 usage error。
- 不带 field 的 `--json` 打印合法字段并成功退出，不执行远程操作。
- 单资源输出 object，集合输出 array，空集合输出 `[]`，不存在的单资源是错误而不是 `null`。
- 输出不增加通用 envelope，不混入提示、进度或 ANSI 控制字符。
- mutation 若返回资源，使用与对应 view 相同的字段名称和语义。
- `null`、缺失字段和空集合的含义由资源投影固定，不能因 renderer 改变。

不提供通用 `-o` / `--output`。初始设计也不内置通用 YAML、`--jq` 或 template renderer。字段选择已经解决 Agent 的主要上下文成本；新增 renderer 必须有三个以上独立、重复出现且外部工具不能清楚解决的用例。

领域资源本身是文本工件时，可以有资源专属 source view。例如 `workflow view --yaml` 返回 Workflow Definition 的原始 YAML；这是资源内容，不是所有命令共享的 renderer。source view 与 `--json` 互斥，只写原文到 stdout，诊断仍写 stderr。

### Streams

- 无界事件与日志一律使用 NDJSON，每行是完整 object。
- 建立 stream 前的失败只写 stderr，不先写半个 JSON object。
- Ctrl-C 取消订阅并退出 `130`；正常服务端结束按命令语义决定 `0` 或 `1`。
- progress 永不写入 stdout。

## Errors and exit status

错误格式是两层文本：

```text
error: <specific cause> [stable_code]
hint: <one executable recovery, only when certain>
```

- stable code 使用小写 snake_case，表示调用方可据此分类的产品错误，不表示内部异常类型。
- parse、文件、互斥参数和 JSON field 错误在本地返回，不发远程请求。
- 未知 area 或 action 返回用法错误 `2`，只展示最近一级的相关 usage；不能回退到根帮助并
  成功退出。
- domain error 保留对象身份、当前状态、要求状态和拒绝原因。
- transport error 区分“确定未提交”与“提交结果未知”。CLI 不自动重发状态修改；只有确认安全时才给出 retry hint。
- 没有确定恢复动作时省略 hint，不能用 “try again later” 掩盖未知原因。
- 默认不输出 stack trace、request body、credential 或内部地址。

退出码只有：`0` 成功、`1` 操作失败、`2` 用法错误、`130` 中断。所有非零路径都必须在 stderr 有一条具体诊断；成功路径不得把 warning 混入 JSON stdout。

## Reliability checks

CLI 的 spec 测试验证公开契约，不依赖真实 Server、进程、Git、网络或墙钟：

- 命令树中每项能力只有一个规范路径；同一 group 内没有同义 action。
- root、group、leaf help 满足各自结构，`--help` 不触发远程依赖。
- 文档、Skill 和 help 中的命令示例都由真实命令树解析。
- help 声明的 JSON fields 与字段选择器完全一致；选择后的 object 不出现额外字段。
- stdout 只含结果，stderr 只含诊断；JSON 与 NDJSON 中没有 ANSI 或进度文本。
- 非 TTY 与 `MOHIST_PROMPT_DISABLED=1` 下没有 prompt 路径。
- target / selector、body / body-file 等互斥输入在本地失败，且没有远程调用。
- 每个错误路径非零退出，并包含 stable code；有 hint 时其命令也能被命令树解析。
- help 文案检查禁止 API route、HTTP method、grain、handler、源码路径、历史 issue 和迁移 alias。

不要用整页 snapshot 作为唯一测试。结构测试锁定必须存在的区块和语义，少量 golden test 只覆盖确实属于公开排版契约的输出。

## Status

当前实现与目标设计的主要差距记录在 [`docs/cli-reference.md`](../docs/cli-reference.md#实装差距)。落地顺序是：

1. 先清理 help 与 Mohist Skill 的上下文边界，不改变行为。
2. 建立字段选择式 JSON、统一 ProjectRef 和 stdout/stderr 契约。
3. 一次性迁移 `workflow/run/session`、规范动词和唯一入口；不保留内建旧 alias。
4. 更新所有用户示例并删除旧路径测试，确保仓库只表达目标语言。

每一步都保持当前命令树、帮助和测试内部一致；不能先发布一套命令、再靠 Skill 解释另一套语法。
