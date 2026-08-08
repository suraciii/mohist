# Agent 执行模型

本文定义 Workflow、Agent、Session、Runner 与 Runtime adapter 共享的抽象边界。
Runtime 特有行为放在 [`runtimes/`](runtimes/README.md)，例如
[`runtimes/opencode.md`](runtimes/opencode.md)。

## 层次

| 层次 | 概念 | 所有者 | 权威状态 |
|---|---|---|---|
| 定义 | Mohist Agent | Agent context | 身份、instructions、config、skills、状态 |
| 工作 | TaskRun | Workflow context | Workflow task 生命周期、结果、输出、恢复 |
| 工作 | AgentJob | Agent context | 一次 Mohist Agent 工作的生命周期与结果 |
| 执行契约 | Action | Workflow context | 一次工作 dispatch 的 `uses` / `with` 输入输出契约 |
| 会话 | AgentSession | Session context | transcript、context、usage、activity、当前 Runtime binding |
| Runtime | Runtime Session | 外部 Runtime | 物理会话与 provider 执行状态 |
| Adapter | OpenCodeRuntime、PiRuntime | Runner 进程 | protocol、进程、事件、状态核对、错误 |

`Inline Agent` 是产品使用方式，不是另一个实体或 bounded context。它表示 Workflow
TaskRun 直接选择 Runtime 特有的 Action 并提供输入，不解析 Mohist Agent。`Agent
定义引用`（`uses: mohist/agent`）同样不是实体：TaskRun 引用 Mohist Agent 的定义
快照执行，工作所有权与 Session 来源不变。

跨上下文统一定义见 [`../CONTEXT.md`](../CONTEXT.md)。本文只定义这些概念的生命周期、
所有权、事件契约和模块边界，不建立第二套术语。

## 调用路径

| 路径 | 工作所有者 | Runner 入口 | AgentSession 来源 |
|---|---|---|---|
| Workflow 直接调用 | TaskRun | Runtime Action adapter | Workflow |
| 启动 Mohist Agent | AgentJob | AgentJob executor | Agent launch（Web、CLI、Agent Connection、事件或提及） |

```text
Workflow: TaskRun -> Runtime Action adapter --+
                                             +-> Runtime adapter -> Runtime Session
Agent: Mohist Agent -> AgentJob executor -----+
```

两条路径共享 Runner 执行能力和 Session 基础设施，但不共享工作所有者：TaskRun 对
Workflow 工作负责，AgentJob 对 Mohist Agent 工作负责。每个入口把已经解析好的
AgentSession 目标交给 Runtime adapter，Runtime 事实写回该 Session。共享 Runtime
代码不能制造 Workflow -> Agent 的领域依赖。

Web、CLI、Agent Connection、事件路由和评论提及只是“启动 Mohist Agent”这条路径的不同
调用来源，不增加第三条执行路径。交互客户端经 [`agent-api.md`](agent-api.md) 提交任务和
上下文；Agent context 统一解析定义并创建 AgentJob，Session context 统一持有会话。Slack
Bot 等 provider adapter 不能自行 snapshot Agent、创建 Runtime Session 或拥有工作结果。
Agent Connection 来源的 Session 与直接启动来源的 Session 共享稳定 Session ID 的观察、
transcript 和后续输入语义。

## Action 语义

`mohist/opencode` 和 `mohist/pi` 是 Runtime 特有的 Action，回答“用这个 Runtime 执行
本次输入”。它们不接收 Agent ID，不解析 Agent 名称，不读取 Agent 定义，也不创建
AgentJob。因此 Workflow 直接使用它们时形成 Inline Agent。

`mohist/agent` 是 Agent 定义引用 Action：task 用 `with.name` 引用 Project 内的
Mohist Agent，dispatch 时由 server 应用层把名字解析为指令与配置快照，task 按
Inline Agent 同一套机制执行。它不是 Runtime 别名，也不是 AgentJob 的 dispatch
通道：工作所有者是 TaskRun，Session 是 Workflow 来源，不创建 AgentJob。Workflow
领域只持有名字 token；解析经 Agent 读侧在 dispatch 应用层完成，Workflow 不引用
Agent 领域类型。解析失败（不存在或已归档）即 task dispatch 失败；每次 dispatch
重新解析，retry 拿到当时的定义。产品契约见
[`../docs/actions/agent.md`](../docs/actions/agent.md)。

AgentJob 路径不能通过公开的 Workflow Action 契约 dispatch。Agent 定义完成解析和
snapshot 后，其 executor 接收由 Agent 拥有的 execution request。Workflow Action
adapter 与 AgentJob executor 可以调用同一个 Runtime 深模块。复用点是 Runtime 实现，
不是 Action。

manual 启动的 AgentJob 省略 workspace 时由 CLI/Web 入口解析当前 Project 的默认
Workspace，并将实际 Workspace 身份写入 Session 与 launch response。dispatch 一旦提供
workspace，`workspace.path` 就必须是非空字符串；畸形 workspace 是无效输入，不能回退到
默认目录。

## 工作生命周期与会话

TaskRun 与 AgentJob 拥有以下决策：

- pending / running / terminal 状态；
- 成功、失败与结果；
- retry、recovery 或 Workflow 推进。

AgentSession 拥有以下事实：

- 按顺序记录的 SessionInput、AgentTurn、回复、tool calls 与 Runtime 状态；
- context 与 usage；
- model / Runtime observations；
- 当前 activity 与 Runtime binding。

Workflow Action adapter 向 TaskRun 报告工作结果，AgentJob executor 向 AgentJob 报告
工作结果；两者都向 AgentSession 报告会话事实。AgentSession 事件不会推进 Workflow，
也不会让 AgentJob 进入终态。工作失败可以成为 transcript 中的诊断，但 Session 不是
工作结果的裁判。

Session 命令不是工作 dispatch。Follow-up 只向现有 AgentSession 追加 SessionInput，不创建
TaskRun 或 AgentJob。它由当前执行处理，或在同一 Session 中形成后续 AgentTurn。Compact
与 Reset 同样只改变 Session；它们不轮换 AgentSession ID。

AgentJob 关联 launch 创建的首个 SessionInput 与 AgentTurn。`Completed` 表示这次 launch
工作成功返回，不表示 AgentSession 关闭，也不对自然语言任务作语义完成判断。首次回复
可以是澄清问题；之后的 Follow-up 由新的 SessionInput 和相应 AgentTurn 记录，不重开或
改写原 AgentJob。需要业务生命周期的输出必须进入 Issue / Workflow，而不是让 AgentJob
等待整段对话结束。

Agent launch 时固定 Instructions、Runtime、Model、Variant 与 Skills，并由该 AgentSession
的后续输入继续使用。Agent 的并发与调度策略由 Mohist 统一执行；入口不能绕过，策略变化
也不强行改写已经开始的执行。

## AgentSession 模型

AgentSession 的结构靠近 Runtime 的物理会话，但拥有 Mohist 的稳定身份：

```text
AgentSession
  Id
  Source
  WorkDir
  Activity
  CurrentBinding?
  Inputs
  Turns
  Transcript
  Context
  Usage

SessionInput
  Id
  Sequence
  Text?
  TurnId?
  Source
  Attachments

AgentTurn
  Id
  Sequence
  Status
  InputIds

RuntimeBinding
  RunnerId
  Runtime
  RuntimeSessionId
```

以下是不变量：

- `Id`、`Source` 与 `WorkDir` 在 AgentSession 生命周期内不变。
- Session parentage is an optional `SessionParentLink` owned separately from immutable `Source`.
  It can only be established for a newly launched child Session and later detached; it never turns
  an Agent launch Source into another Source. The complete tree contract is
  [`subagents.md`](subagents.md).
- `CurrentBinding` 是当前路由事实，可以整体替换；AgentSession 不保存物理 Session 历史。
- `Transcript` 是一个按 AgentSession 顺序追加的会话记录，不按物理 Session 或其它
  子实体拆分。
- `Context` 描述当前 Runtime Session 的上下文；binding 替换后从空开始。`Transcript`
  与累计 `Usage` 不随 binding 替换清空。
- 同一 AgentSession 同时最多有一次 Runtime 执行；该串行约束使 transcript 的 Session
  内顺序足以表达会话。
- Session 最多有一个 active Turn，等待执行的 Input 与 Turn 保持 Session 内顺序。
- 已经接受的 Input 不会因容量限制被丢弃、覆盖或换 ID；容量不足时拒绝新的输入。
- 用户输入必须包含可见文本或明确附件；attachment-only 输入不生成隐藏 prompt。
- AgentSession 没有 `completed`、`failed`、`stopped` 或 `closed` 生命周期。

`CurrentBinding` 允许初始为空。首次执行先创建物理 Session，再把 binding 持久化；只有
binding 持久化成功后才能提交输入。

## Activity 与 transcript

### Activity

AgentSession 的活动状态只有：

| 值 | 含义 |
|---|---|
| `idle` | 没有未终结 Turn，可以开始新的 Turn、Compact 或 Reset |
| `active` | 至少有一个 queued 或 active Turn；Turn 状态区分等待与 Runtime 正在处理 |
| `unknown` | 无法确认输入是否已被接受或执行是否已停止；不得当作安全空闲 |

新 AgentSession 的初始 activity 是 `idle`，此时允许 `CurrentBinding` 为空。

状态转换为：

```text
idle + input accepted                    -> active
active + follow-up accepted              -> active
active + all Turns confirmed terminal    -> idle
active + stop result uncertain        -> unknown
idle + input acceptance uncertain     -> unknown
unknown + runtime evidence             -> active | idle
```

一次执行完成、失败或取消只让 activity 回到 `idle`。具体结果由 TaskRun、AgentJob 或
transcript 中的 Runtime 诊断表达，不能成为 AgentSession 终态。Runtime 进程退出、缓存
回收或持久化文件保留同样不能推导 Session 已关闭。

### Transcript 契约

SessionInput 与 AgentTurn 是 AgentSession 拥有的子记录，不是可独立寻址和修改的聚合。
Session 是输入顺序、Turn 归属和状态转换的唯一写入权威。Transcript 仍是扁平、按 Session
顺序追加的会话事实；Input 与 Turn ID 只提供稳定关联，不建立第二份消息树或物理 Session
历史。

每条被接受的输入都对应一个稳定 SessionInput，同一调用重试不能复制输入。AgentTurn 记录
一段连续处理的排队、执行和结果；一个 Input 只属于一个 Turn。消息、reasoning、tool、usage、
model、provider retry、compaction 和状态事实继续按发生顺序进入同一 transcript。

Input 是否已被 Runtime 接受、Turn 是否仍在执行，都由 Session 记录。结果不确定时保留
`unknown` 并核对原记录，不能换一个新 ID 自动重投。Session activity 只表达当前是否仍有
执行，不重复表达 TaskRun 或 AgentJob 结果。

已有 binding 被替换时，`session.context_reset` 是 transcript 中的用户可见边界：

```json
{
  "type": "session.context_reset",
  "payload": {
    "reason": "reset | runtime-change | missing-recovery",
    "observedAt": "2026-07-22T10:03:00Z"
  }
}
```

该事实只表达“后续 Runtime 上下文从空开始”，不携带旧或新物理 Session ID，也不建立
binding 历史。首次从无 binding 建立物理 Session 时不写该事实。替换 binding 与写入
`session.context_reset` 必须原子完成；该事实在替换后的下一条 `session.input` 之前。

`session.closed` 不属于目标 DSL：一次执行结束不关闭 Session。
`session.followup_completed` / `session.followup_failed` 同样不属于目标 DSL：Input 与 Turn
分别表达受理和执行，不能用一个 follow-up 事件混合两者。

消费者不能从历史错误、完成或停止事实推导当前 activity。当前 activity 由 Session
状态和最新 Runtime 证据决定。

## Follow-up 与 Cancel

空闲 Session 收到 Follow-up 时开始新的 Turn。执行中的 Session 在 Runtime 支持时把输入加入
当前 Turn，否则按顺序等待后续 Turn；`unknown` 时拒绝新输入并先核对状态。API 的同步结果
只确认 Input 是否已被 Mohist 接受，不能假装 Runtime 已经完成处理。

定时输入是到点才投递的一次性 follow-up：Server 在到期时经同一受理路径把一条普通
`SessionInput` 追加给目标会话，不创建新输入类别、调度器或 Session 终态。完整契约见
[`subagents.md`](subagents.md) 的「定时输入」节。

Follow-up 命令只需要三种同步结果：

- `accepted`：Mohist 已持久接受 SessionInput，它可能仍在排队；
- `rejected`：Mohist 已确认没有接受输入；
- `unknown`：无法确认是否接受，不能自动重新发送。

调用幂等键用于找到同一个 SessionInput，不是 AgentSession 内的另一个领域实体，也不用于
把 transcript 分组。`unknown` 后只能使用同一调用身份核对或重试；创建新身份重新发送可能
产生重复副作用。

Compact 与 Reset 这类 recovery 命令在调用方省略显式幂等键时，由 grain 每次生成唯一键
（与 `operationId` 同格式），不再退化为固定值；显式提供幂等键时同键重放、异键 join
同一 in-progress reservation 的语义不变；省略键的重试因此不再跨操作幂等——已完成
reservation 不会被后续缺省调用误命中，缺省调用落入 `BeginSessionCommandAsync` 开启新
操作。需要重试幂等的调用方必须显式提供键。

Cancel 只针对当前未终结 Turn。等待中的 Turn 可以直接取消；正在执行的 Turn 请求 Runtime
停止。无法确认停止结果时，Turn 与 Session activity 保持 `unknown`，不能伪造 idle。首个
Turn 的结果由 AgentJob 裁定，后续 Turn 的取消不改写已经终结的 AgentJob。

## AgentSession 来源

每个 AgentSession 有且只有一个不可变来源。

### Workflow 来源

使用 `(projectId, workflowRunId, sessionName)` 寻址。同一 WorkflowRun 内复用相同名称
会继续逻辑会话。省略显式名称时使用 Work ID，避免无关 task 意外共享 context。

### Agent launch 来源

每次启动 Mohist Agent 时创建，并关联已解析的 Agent ID。一个 Mohist Agent 可以创建
多个 AgentJob 和 AgentSession。之后编辑或归档 Agent，不改变 Session 来源或启动时的
执行 snapshot。

相同 prompt、model、Runtime、workspace 或配置不会合并两个来源。Session 不能从
Workflow 来源迁移为 Agent 来源，反之亦然。

来源特有的 route 只是查询和便利入口，最终都解析为以 `sessionId` 标识的规范
AgentSession 资源。Follow-up、Compact、Reset、transcript 与查询都作用于该资源，
不能实现第二套 Session 生命周期。

## 当前 Runtime binding

AgentSession ID 是逻辑会话的稳定身份。Runtime Session 身份是外部物理维度：

```json
{
  "runnerId": "runner-...",
  "runtime": "opencode",
  "runtimeSessionId": "ses_..."
}
```

正常执行、retry、Follow-up、Compact、model / variant 变化和 Runner 重启都复用当前
binding。Reset、Runtime 变化和已确认的 Runtime Session 缺失恢复可以整体替换 binding，
但不能改变 AgentSession 身份、来源或工作目录。

AgentSession 只保存 `CurrentBinding`。旧 binding 不进入 aggregate、DTO 或独立查询模型；
已有 transcript 也不会按 binding 拆分。Reset、缺失恢复或 Runtime 变化只在 transcript
记录一次 `session.context_reset`，说明后续 Runtime 上下文从空开始，不记录物理 Session
沿革。

替换使用完整 expected binding 做 compare-and-swap：

```text
replaceBinding(expected, candidate):
  require activity == idle
  require currentBinding == expected
  require candidate was created for AgentSession.workDir
  currentBinding = candidate
```

Runtime event 必须携带产生它的 `runtimeSessionId`。它不等于 current binding 时，Server
拒绝该事件；旧物理 Session 的迟到事件不能改变当前 activity 或 transcript。

物理 Session 的缓存、文件、进程资源与保留策略属于 Runtime adapter。binding 被替换
不要求 Mohist 删除、关闭或继续查询旧物理 Session。

## Runtime Session 缺失恢复

缺失恢复是 Session 对当前 binding 的修复，不是 Prompt retry，也不是 Workflow
recovery。它只在一条新的独立输入尚未被 Runtime 接受时发生。成功后，同一个工作
attempt 继续执行，不消耗 Workflow recovery budget。

### 触发条件

以下条件必须同时成立：

1. AgentSession 的 `activity` 为 `idle`；
2. 执行位于当前 binding 的 `runnerId`，Runtime 与工作目录仍匹配；
3. 该 Runner 上的 Runtime adapter 用确定性证据确认 `runtimeSessionId` 已不存在；
4. 本次输入尚未写入 transcript，也尚未向 Runtime 提交；
5. 替换 binding 和随后记录输入时，Server 看到的 expected binding 都仍是 current。

Runtime 不可用、超时、权限失败、响应不兼容、数据损坏或任何无法区分“暂时无法读取”
和“确定不存在”的结果都不满足条件。请求落在另一个 Runner 也不是 missing；它必须
路由回 binding 所属 Runner 或明确失败，不能借缺失恢复迁移 Runner。

### 解析与替换顺序

```text
expected = AgentSession.currentBinding

if expected is absent:
    candidate = Runtime.create(requiredRuntime, AgentSession.workDir, inputOptions)
    selected = Session.replaceBinding(expected = absent, candidate)
else:
    require currentRunnerId == expected.runnerId
    resolved = Runtime.resolve(expected)

    if resolved is ready:
        selected = expected
    else if resolved is definitely-missing:
        candidate = Runtime.create(expected.runtime, AgentSession.workDir, inputOptions)
        selected = Session.replaceBinding(expected, candidate)
    else:
        fail without changing the binding

Session.recordInput(expectedBinding = selected, input)
Runtime.submitInputExactlyOnce(selected, input)
```

自动恢复最多创建一个 candidate。新 candidate 创建或 binding 持久化失败时，不提交输入。
已经创建但未能绑定的 candidate 只形成诊断，不引入补偿协议，也不复制物理会话数据。

Runner 只报告 Runtime 的 resolve / create 事实；`replaceBinding` 与 `recordInput` 都由
Server 裁决。两次写入都比较 expected binding，避免过期恢复覆盖 Reset、Runtime 变化
或另一轮恢复。只有输入持久化成功后 Runner 才能提交给 Runtime。

### 操作边界

| 操作 | 确认缺失时自动替换 | 原因 |
|---|---:|---|
| TaskRun 或 AgentJob 提交新输入 | 是 | 输入尚未提交，可以在空上下文继续 |
| AgentSession 空闲时的 Follow-up | 是 | 它将开始新的执行，使用相同提交顺序 |
| 执行中的 Follow-up | 否 | 输入目标是当前物理执行，替换后语义不同 |
| Compact | 否 | 缺失的上下文无法压缩 |
| Cancel | 否 | 新物理 Session 不是原执行目标 |
| Reset | 不属于自动恢复 | 用户主动建立空上下文，旧 Session 缺失也不阻止 Reset |

自动恢复不从 Mohist transcript 重放消息、Prompt 或 tool call。Transcript 是审计与展示
记录，不是重建 Runtime 上下文的命令来源。

## Runtime 变化与 Reset

Runtime 变化或 Reset 只在 `idle` 时执行。Runner 先创建新的空物理 Session；Server
只在 expected binding 仍是 current 时整体替换。新 Session 建立失败时保留原 binding。

Reset 不改变 Runtime；Runtime 变化可以改变 `runtime` 和 `runnerId`，但不能改变
AgentSession 工作目录。两者都保留已有 transcript，不迁移或重放 Runtime 上下文，
也不建立物理 Session 历史。

## 模块所有权

- Workflow 拥有 TaskRun 和 Workflow Action 契约，不解释 Session transcript。
- Agent 拥有 Mohist Agent 与 AgentJob，不解释 Session activity。
- Session 拥有 AgentSession 身份、source、workDir、SessionInput、AgentTurn、activity、current
  binding、transcript、context 与 usage。
- Runner 执行已经解析的工作，创建或恢复 Runtime Session，并报告物理事实。
- Runtime adapter 隐藏 SDK / protocol、缓存、进程、文件、事件核对和错误分类。
- Web 和 CLI 只消费 Server 给出的 activity、current binding 与 transcript，不自行从
  历史结果推导 Session 状态。

Server 是 binding 与 activity 的唯一状态裁判。Runner 不能自行决定 current binding
已经改变，也不能因为 Runtime 进程退出就关闭 AgentSession。

## 测试边界

默认测试不访问真实 Runtime、网络、进程、文件系统 Session 或墙钟。至少覆盖：

- 同一 AgentSession 跨 task、retry、Follow-up 与 Runner 重启复用 current binding；
- SessionInput 与 AgentTurn 的身份、归属和顺序在进程重启后保持不变；
- 相同输入重试不产生重复记录，已接受输入不会因背压丢失；
- binding 替换与 `session.context_reset` 原子持久化，且事件不包含物理 Session 沿革；
- 一次执行完成、失败或取消后 activity 回到 `idle`，没有 Session 终态；
- 停止或输入受理不确定时进入 `unknown`，不会自动重放；
- Reset、Runtime 变化和 confirmed missing 在 `idle` 时原子替换 current binding；
- stale expected binding 与旧 Runtime Session 事件被拒绝；
- binding 替换不创建新 AgentSession、不保存物理 Session history，也不复制物理会话数据；
- TaskRun / AgentJob 结果与 AgentSession activity 互不覆盖。

## 实装差距

SessionInput 与 AgentTurn 尚未作为上述稳定子记录落地；当前 transcript 不能完整区分
Input 受理与 Turn 排队、执行和结果。

#484 已落地扁平 transcript 与独立 activity：不再写入 `session.closed` /
`session.followup_*`，状态与命令资格只读当前 activity；终态以 `session.activity`
（activity=idle + 终态 status）持久化。

残留差距：退役词汇仍留在三条路径上——server 仍接受（但已无生产者）`session.closed`
runtime 事件并映射为同名片段类型；事件 feed 仍把终态 `session.activity` 记录以
`session.closed` 类型展示；web 仍保留该名字的标签与视图处理。#496 负责清除。

Runtime Session 确认缺失后的自动创建与 expected-binding replacement 尚未覆盖全部入口；
当前部分路径仍要求用户 Reset。该差距按本文的最小 current-binding 模型实施。

命名 Agent 的 launch 与 `mohist/agent` attempt 已按 Agent 定义固定 Instructions、Runtime、
Model、Variant 和 Skills；客户端 task/context 输入不能覆盖这些字段。显式
`mohist/opencode` 与 `mohist/pi` Action 仍由各自的 `uses` 选择 Runtime。
