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

## #378 目标 spec：Input/Turn 生命周期与 Runtime 恢复

本节是 AgentSession 执行契约的目标定义。它补充并细化前文的 AgentSession、Input、Turn
和 binding 模型；下文定义的公共投影优先于实现内部的 `executing`、`completed` 或
`failed` 等词。它不引入新的 endpoint、CLI 语法或事件 DSL。CLI 与 Web 只消费同一份
Server canonical 状态，Workflow 后续复用这份状态，不在客户端各自推导生命周期。

### 对象关系与责任

| 对象 | 领域责任 | 与其它对象的关系 |
|---|---|---|
| Agent | 可长期复用的命名实体，保存定义与配置引用 | 一个 Agent 可产生多个 AgentJob 和 AgentSession |
| Persona | Agent 使用的配置模板 | 被 Agent 解析并快照，不拥有执行记录 |
| AgentJob | 一次 launch 工作的拥有者与结果裁判 | 关联一个首个 Input/Turn；不拥有 follow-up 的生命周期 |
| AgentSession | 一个可继续交互的公共逻辑会话 | 稳定拥有 Inputs、Turns、transcript、context、usage 和 current binding |
| Input | 一次用户或系统提交的执行意图 | 持有稳定 Input ID，恰好关联一个 Turn；重试不创建副本 |
| Turn | 一个 Input 的排队、执行、观察和结果记录 | 持有稳定 Turn ID；同一 Session 的 Turn 串行 |
| Runtime Session | Runner/Runtime 中的物理执行上下文 | 可消失、重建或换绑；不改变 AgentSession 身份 |
| AgentJob executor | 将 launch 意图交给 Session 与 Runner | 不绕过 Session 记录，也不把 Runtime 事件当成公共结果 |

AgentSession 是公共逻辑身份，不是 Runtime Session 的别名，也不是 AgentJob 的别名。
AgentJob 只代表 launch 的一次工作；follow-up 追加到原 AgentSession，不新建 AgentJob。
Persona 的变更不回写已创建 Session 的执行快照。Runtime Session 的丢失、重建、Runner
重启或换绑都必须映射回同一个 AgentSession、Input 和 Turn 记录。

每次 launch 或 follow-up 都必须拥有明确的 Workspace 和 target，且它们进入持久记录与
canonical 返回值。CLI 省略 Workspace 时，入口先解析当前 Project 的真实默认范围，再
返回和持久化该范围；不得返回空值、仅返回“默认”或让 Web 再猜目录。

### 稳定身份与 canonical 投影

Server 在受理事务中生成或确认 `AgentJobId`、`AgentSessionId`、`InputId` 和 `TurnId`。
这些 ID 一经持久化不因排队、Runner 重启、binding 替换、重试、Compact 或 Reset 改变。
RuntimeSessionId 只标识当前物理 binding；它可以替换，不能作为公共逻辑 Session 的
身份。重复提交通过同一请求身份命中原 Input/Turn，不能创建第二份输入或第二个副作用。

CLI 和 Web 的最小 canonical 结果包含以下语义字段；具体 JSON 外壳仍由现有 API/CLI
约定决定，本 spec 不发明新语法：

实现优先固化 CLI 的 canonical JSON，再由 Web 复用同一字段，随后才由 Workflow 接入；
通用外部 API 的认证、幂等和断线续读留给 #387。

| 字段语义 | 规则 |
|---|---|
| AgentJobId / AgentSessionId | launch 返回 Job 与公共 Session；follow-up 只返回已有 Session |
| InputId / TurnId | 每次接受的意图与其 Turn 的稳定身份，查询和后续状态沿用 |
| accepted | 只表示 Server 已持久接受 Input；`accepted=true` 不表示已经 running |
| status | 当前 Turn 的 canonical 状态，使用 `queued`、`running`、`idle`、`terminal`、`unknown` |
| result | 只有已知结果才填充；`terminal` 之外不得伪造成功、失败或取消结果 |
| error / next action | 失败或 Unknown 时给稳定的用户可理解原因和下一步；不暴露 provider 事件名 |
| workspace / target | 返回实际绑定的 Workspace 与 target，供 CLI/Web 展示和复用 |

`accepted` 是 Input 的受理事实，`status` 是 Turn 的观察事实。一个已接受的 Input 可以
长期处于 `queued`，也可以因 Runner 不可用进入 `unknown`；只有执行已被 Runtime 确认
开始时才是 `running`。Session 级 `activity` 仍可为 `idle`、`active` 或 `unknown`，但
它不能替代 Turn status：`activity=idle` 仅表示当前没有被确认正在执行的工作，不表示
最近一个 Turn 已有最终结果。

Turn status 的定义如下：

| 状态 | 进入条件 | 语义与允许转移 |
|---|---|---|
| `queued` | Input 已接受且 Turn 已持久化，尚未被 Runtime 确认开始 | 可转 `running` 或在受理/派发事实不确定时转 `unknown` |
| `running` | 当前 binding 的 Runtime 已确认接收并执行该 Turn | 可转 `idle`、`terminal` 或因结果不确定转 `unknown` |
| `idle` | 当前 Turn 暂无 Runtime 执行，但最终结果尚未被 Server 归档 | 可转 `terminal`；若观察窗口失去确定性则转 `unknown`，不能当作成功 |
| `terminal` | Server 已持久化不可逆的成功、失败或取消结果 | 终态，不再重放、不再改变结果 |
| `unknown` | 无法确认 Input/side effect/执行结果是否已发生 | 非终态；只能由权威观察转 `queued`、`running`、`idle` 或 `terminal`，不能自动重投 |

`idle` 与 `terminal` 的分界是“有没有已知最终结果”，不是“Runtime 进程是否还活着”。
没有当前执行的 Session 可以是 `activity=idle`，而某个尚未归档结果的 Turn 仍为 `idle`；
公共 UI 不得把这两者显示成已完成。`unknown` 也不是 `idle`，因为它不能安全地接受
会产生副作用的新输入。内部 `Executing` 映射为公共 `running`；内部 Completed/Failed/
Cancelled 只有在结果已持久化时才映射为 `terminal`，不能把内部枚举直接暴露给 CLI/Web。

### launch 与 follow-up 生命周期

两类调用都经过同一条逻辑序列，差别只有 AgentJob 是否在 launch 时创建：

```text
request -> accept -> queue -> execute -> result
```

1. `request` 校验 Agent、Persona 快照、Workspace、target、输入身份和当前 Session。
   这一步不承诺 Runtime 已可用。
2. `accept` 在 Session 事务内持久化 Input、Turn 及其关联 ID。成功返回 `accepted=true`
   和稳定 InputId/TurnId；此时状态通常为 `queued`，不能写成 `running`。
3. `queue` 将 Turn 放入该 Session 的有序执行队列。相同 Session 严格只有一个被确认
   执行的 Turn；follow-up 在前一 Turn 未终结时排队，不插队、不合并、不覆盖。
4. `execute` 由绑定 Runner 核对或恢复 Runtime。只有收到当前 binding 的开始事实后，
   Turn 才转为 `running`。不同 AgentSession 可并行；本节不定义容量 claim/release、
   队列容量或 capacity view。
5. `result` 只由 Server 根据当前 binding、关联 InputId/TurnId 和权威结果归档。已知
   最终结果使 Turn 进入 `terminal`；Runtime 先返回空闲而结果仍未确定时保持 `idle`。

launch 为一次新的 AgentJob 创建首个 Input/Turn，并将 JobId、SessionId、InputId、TurnId
一起返回。follow-up 不创建 AgentJob，只追加 Input/Turn。请求提交后断线，调用方只能用
原请求身份查询同一记录；不能用新的请求身份猜测或重发。

同一 Session 的串行约束是生命周期事实，不是容量策略：前一 Turn 处于 `queued`、
`running` 或 `unknown` 时，后续 Input 只能继续排队或被明确拒绝，不能并行提交到同一
Runtime。#382 负责 max-concurrent-runs 的 capacity claim/release 与容量视图；#378
只要求 Session 内顺序和状态事实，不实现、复制或固化 #382 的容量规则。

### Runtime 恢复状态机

恢复状态机由 Server 裁判状态、Runner 事实和当前 binding CAS 组成。它不依赖客户端
猜测，也不把 HTTP 超时当作 Runtime 缺失：

```text
Bound
  | disconnect / runner restart / probe unavailable
  v
ObservationUnknown -- authoritative present --> Bound
  | authoritative definitely-missing
  v
RecoveryWindow
  | CAS + create empty Runtime Session succeeds
  v
Rebound -> Bound
  | create/CAS/runner failure
  v
RecoveryFailed
```

- `Bound`：当前 binding 是唯一目标。正常 retry、follow-up、Compact、Reset 和 Runner
  重启都先复用它；Runner 重连后重新报告当前 physical session 的事实。
- `ObservationUnknown`：断线、超时、Runner 不可达、权限错误、非 404 错误、格式错误或
  任何不能证明“不存在”的结果。保留原 binding，不创建候选，不 replay，不自动换绑。
- `RecoveryWindow`：Runner 已明确报告当前 Runtime Session 不存在，Server 正在为同一
  AgentSession 创建空 Session 并用完整 expected binding 做 CAS。窗口期间不允许第二个
  recovery 操作覆盖第一个；查询返回恢复中和原稳定 IDs。
- `Rebound`：候选 Runtime Session 创建成功、binding 原子替换成功，并写入一次
  `session.context_reset(reason=missing-recovery)`。AgentSession、Workspace、target、
  Input/Turn ID 和公共 transcript 保持不变。
- `RecoveryFailed`：创建、Runner 路由、CAS 或持久化失败。原 binding 不被伪造替换；
  返回可行动的原因与下一步。下一步只能是继续查询恢复窗口、等待 Runner ready、使用
  Reset 建立新上下文或联系管理员处理 Runner/Workspace，具体选择取决于失败事实。

恢复触发必须满足“权威确认缺失”。Runner 重启本身、连接断开本身和读超时都不满足；
它们只进入 `ObservationUnknown`，待同一 Runner 的 probe 给出 `present` 或
`definitely-missing`。物理 Session 存在时必须继续使用它，不能因重连而新建。

若当前没有未决副作用（Session 空闲且无 queued/running Turn），确认缺失后可自动创建
空 Runtime Session、换绑，并让后续输入继续执行。若当前 Turn 已经可能提交到旧 Runtime，
该 Turn 的最终结果转为 `unknown`；恢复仍可为同一个 AgentSession 重建和换绑，但绝不把
旧 Input、prompt、tool call 或 side effect 自动 replay 到新 Runtime。新的输入必须等
恢复状态允许后才可接受，且不能借新 InputId 掩盖旧 Turn 的 Unknown。

恢复成功不等于原 Turn 成功。它只说明公共 AgentSession 获得了新的可用 Runtime 上下文。
原 Turn 只有收到旧 binding 的权威终态才可进入 `terminal`；旧 binding 的迟到事件因
RuntimeSessionId 不匹配被丢弃。恢复失败也不关闭 AgentSession，不生成新的 SessionId，
不把错误压缩成 `Session failed`。

missing-recovery 是 `replaceBinding` 的唯一例外边界：若旧 Turn 已可能产生 side effect，
Server 先冻结该 Turn 为 `unknown`，再允许用完整 expected binding 换绑；这不等于把
Session activity 变成安全 `idle`，也不允许新的 Input 通过旧 Turn 的空档进入 Runtime。
Reset、Runtime 变化和无未决副作用的普通换绑仍要求 Session 可安全进入 `idle`。因此 CAS
同时保护“不能覆盖新 binding”和“不能把 Unknown 当作空闲”的两个不变量。

状态查询或相同请求身份的重试只做 observation：

- queued 的重复请求可重新返回原 ID 和当前状态，但不得追加 Input；
- running、idle、terminal、unknown 的重复请求只返回原记录，不重新 dispatch；
- unknown 只能被权威事件或显式用户操作推进，不能因为客户端重试而变成 queued；
- side effect 结果不确定时不自动 replay；用户应先查询，仍不确定时按 `next action` 选择
  Reset 或人工核对。

### 断线、重复提交与恢复窗口的确定性规则

| 场景 | 保留事实 | 自动动作 | 公共结果 |
|---|---|---|---|
| 请求后客户端断线 | 已持久化的 Input/Turn 与原请求身份 | 不重发；重连后查询 | 原 InputId/TurnId，状态按 Server 当前事实 |
| 重复提交同一请求身份 | 原 Input/Turn | 返回原记录；仅 queued 可继续同一 dispatch | `accepted` 不重复，副作用至多一次受理 |
| 不同身份再次提交相同文本 | 原记录与新请求身份不同 | 不推断等价，不自动去重 | 新请求按正常校验，必要时拒绝或产生新 Input |
| Runner 重启 | current binding | Runner 重连 probe；存在则复用 | 不改变 Session/Input/Turn ID |
| 连接断开/超时 | current binding 与可能的活动事实 | 保留 binding，进入 observation unknown | 活动中的 Turn 为 `unknown`，不当作 idle |
| 明确确认 Runtime 缺失且无未决副作用 | Session 记录 | 创建空 Runtime、CAS 换绑 | 同一 Session，后续 Turn 可 `queued` |
| 明确确认缺失但旧 Turn 可能已提交 | 旧 Turn 与其副作用不确定性 | 可为未来操作换绑，但不 replay 旧 Input | 旧 Turn `unknown`，给查询/Reset next action |
| 恢复失败 | 原 binding 与原记录 | 不换绑、不关闭 Session | `recovery_failed`，原因具体且可行动 |

恢复窗口不能靠墙钟轮询制造结论。若实现需要 deadline，必须注入 `TimeProvider` 或等价
fake，并把“窗口过期”作为持久化的恢复结果；测试不能用 sleep 或当前时间碰运气。

恢复失败的公共错误至少区分以下可行动语义；字段名沿用现有 canonical error/result
外壳，不把这些值实现成新的命令语法：

| 原因 | 用户可观察含义 | next action |
|---|---|---|
| `recovery_in_progress` | 同一 Session 已有恢复操作占用窗口 | 查询原 Session/Input/Turn，等待该操作给出结果，不重复提交 |
| `runtime_unavailable` | Runner 或 Runtime 暂不可达，不能证明缺失 | 等待 Runner ready 后用原请求身份查询；不要新建请求 |
| `runtime_missing_unconfirmed` | probe 结果不足以证明 physical session 不存在 | 继续查询或让 Runner 重连 probe；不要 Reset 代替事实判断 |
| `recovery_failed` | 已确认缺失但 create/CAS/持久化失败 | 检查 Runner、Workspace 和权限；必要时显式 Reset，仍保留原 Session 诊断 |
| `turn_outcome_unknown` | 原 Turn 可能已产生副作用但没有权威结果 | 查询原 Turn；仍 Unknown 时按产品流程人工核对或 Reset，绝不自动 replay |

### Compact、Reset 与公共上下文边界

Compact 与 Reset 都是 AgentSession 的上下文边界操作，不是 Workflow Action，也不创建
新的 AgentSession、AgentJob、Input 或 Turn。两者保留已有公共 transcript、稳定 IDs、
Workspace、target 和累计 usage；它们只改变后续 Runtime context 的边界。

- Compact 在 Session 可安全进入边界时请求 Runtime 压缩当前上下文；成功后继续使用同一
  binding，后续输入从新的上下文边界开始。
- Reset 建立空上下文；必要时创建新的 Runtime Session 并整体替换 current binding，
  但保留旧 transcript 和逻辑 Session 身份。
- 运行中、Unknown 或恢复窗口内不能假装已经完成 Compact/Reset；返回当前状态和下一步。
- 边界记录是公共领域事实，不把 provider 的 raw event、内部 session ID、tool 细节或
  重建诊断直接写入公共 transcript。公共 transcript 的默认投影由 #384 负责；历史和
  Session timeline 展示由 #385 负责，本设计只规定“旧记录保留、边界可观察、内部事件不
  外泄”。

### 可观察不变量与场景矩阵

实现必须能通过 Server fake 和 Runner/Runtime fake 观察以下不变量：

1. `accepted=true` 必有持久 InputId/TurnId；同一请求身份永远指向同一对 ID。
2. 一个 Input 恰好属于一个 Turn；一个 Session 内 Turn 的顺序持久且不可被重排。
3. `running` 只来自当前 binding 的执行事实；旧 Runtime 事件不能改变当前状态。
4. `terminal` 有持久最终结果；`idle` 没有最终结果，`unknown` 不能被误报为 idle。
5. 一个 Session 同时最多一个 running Turn；不同 Session 可以并行；不出现容量策略断言。
6. confirmed missing 才能换绑；不确定错误保持 binding，side effect 不自动 replay。
7. 换绑不改变 AgentSession、Input、Turn、Workspace、target 或既有 transcript。
8. Compact/Reset 之后旧 transcript 可查询，内部 Runtime 事件不会直接成为公共消息。
9. cancel/stop 的既有语义不变；停止结果不确定时保持 Unknown，不自动重投。
10. 恢复失败包含具体 reason 与 next action，且 AgentSession 仍可查询和诊断。

| 场景 | 初始条件 | 关键断言 |
|---|---|---|
| 正常 launch | 无 binding、空 Session | 返回稳定 Job/Session/Input/Turn，accepted 后先 queued，再 running，最终 terminal |
| 正常 follow-up | Session idle、已有 transcript | 不新建 Job；同一 Session 新 Input/Turn 串行执行 |
| follow-up 排队 | 前一 Turn running | 后一 Turn 保持 queued；前一 Turn 终结后才可 running |
| 断线 | Turn running，Runner 不可达 | binding 保留，Turn unknown；恢复前不重发 |
| duplicate submit | 相同请求身份重试 | 返回原 Input/Turn，无重复 transcript、Turn 或 Runtime submission |
| Runtime disappear | probe 明确 missing | 空 Runtime 创建、CAS 换绑、同 Session context boundary |
| ambiguous disappear | timeout/非 404/Runner restart | 不换绑、不 replay；进入 observation unknown |
| recovery success | candidate 与 CAS 成功 | 公共 Session 可继续查询；原未决 Turn 仍按事实为 terminal 或 unknown |
| recovery failure | create/CAS/Runner 失败 | 保留原 binding，返回具体错误与 next action |
| Compact | 可安全边界 | transcript 保留，后续 context 有边界，无 raw Runtime event 外泄 |
| Reset | 可安全边界 | 同一 Session/IDs，空 context；绑定替换按 CAS |
| cancel/stop 回归 | queued/running/unknown 各一例 | 不改变既有 cancel/stop 语义；不确定停止不变 idle |

### 架构、测试与实现分批

Server 持有受理、ID、队列、binding CAS、状态投影和恢复裁判；Runner 只执行、probe、
create、发出带 RuntimeSessionId 的事实；Runtime adapter 将 SDK/文件/协议错误归类为
present、definitely-missing 或不确定失败。CLI/Web 不解析内部事件名，也不根据时间戳或
本地轮询自行拼接生命周期。

测试使用注入的 Server store、Runner registry、Runtime probe/create/submit seam、事件
outbox、idempotency store 和 `TimeProvider` fake。禁止真实网络、进程、Runtime SDK、文件
系统 Session、数据库或墙钟；每个场景都能固定输入事件顺序并断言持久状态、canonical
投影和副作用调用次数。spec 测试覆盖跨组件行为，unit/architecture 测试覆盖状态机、
binding CAS、投影映射和依赖边界；测试时长遵循 `design/testing.md`。

建议按可独立验收的价值分批：

1. **稳定受理记录**：launch/follow-up 都能持久化并查询同一 Input/Turn ID、accepted、
   Workspace、target 和 canonical 状态；这是 #378 必须先落地的最小用户价值。
2. **串行 Turn 与公共投影**：实现 queued/running/idle/terminal/unknown 的映射、同 Session
   顺序和 duplicate observation；CLI 与 Web 能展示同一事实。这不是 #382 的容量实现。
3. **确定性 Runtime 恢复**：加入 present/missing/ambiguous 分类、recovery window、
   current-binding CAS、同 Session 换绑和 Unknown/no-replay；覆盖 Runner 重启、断线和
   恢复成功/失败。
4. **上下文边界与回归**：实现 Compact/Reset 的边界事实、旧 transcript 保留、actionable
   failure，并回归 cancel/stop；不改默认 transcript 投影。

#378 依赖既有 Agent 配置与启动契约，但不包含 #377 的 Agent 配置/启动体验。#382 单独
负责 max-concurrent-runs 的 capacity claim/release、容量排队视图和其策略测试；#384
单独负责默认 transcript 公共投影；#385 负责历史/Session timeline 展示；#387 负责
外部 API 的认证、幂等、断线续读。#378 只提供这些边界可复用的内部 canonical 状态，
不提前设计它们的 endpoint 或 UI。

### 方案比较与选择

方案 A 是“Runtime Session 作为公共 Session”：客户端直接以 RuntimeSessionId 查询，
Runtime 丢失就创建新物理 Session 并重放旧 transcript。它实现短期恢复简单，但会让
provider ID 泄露到公共契约，Runner 重启改变逻辑身份，且重放会在 side effect 不确定时
产生重复操作；也无法稳定关联历史 Input/Turn。

方案 B 是“AgentSession 逻辑身份 + current binding + Server canonical 状态”：Input/Turn
和结果始终归 AgentSession，RuntimeSessionId 只作当前物理路由；只有权威 confirmed missing
才按 expected binding CAS 换绑，旧副作用不重放，结果不确定保留 Unknown。它需要额外的
probe 分类、恢复窗口和 CAS 测试，但能保持稳定 ID、同 Session 串行和 CLI/Web 一致，
也能在 Runner 重启时复用实际存在的 Runtime。#378 选择方案 B；其主要失败模式是 Runner
不可达、probe 不确定、候选创建失败和 CAS 冲突，均通过保留旧 binding、明确 Unknown 或
actionable recovery failure 处理，而不是猜测成功。

## Current gap

以下是基于当前 master `89f46a6d17fda766e2a127ffee7b326c2bc94c19` 的实现差距，不改变上文
目标：

- `packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.cs:574-630` 已有
  Input/Turn 记录和内部状态枚举，但它们尚未完整成为本文规定的公共稳定子记录；
  `packages/server/src/Mohist.Server/Agent/Services/AgentLaunchObservationAssembler.cs:171-178`
  仍把内部 `Executing`、`Completed`、`Failed`、`Cancelled` 直接映射为公共字符串，尚未
  统一为 `running`、`terminal` 和独立的 `idle` 语义。
- `packages/server/src/Mohist.Server/Api/AgentSessionFollowupRoutes.cs:329-399` 已返回
  follow-up 的 InputId、TurnId、accepted 和 TurnStatus，但 launch、follow-up、查询和
  Web projection 仍需收敛到同一 canonical 字段、Unknown 语义和 actionable next action。
  `packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:144-150` 的非
  canonical 旧入口仍可能产生空的输入/Turn 身份，必须由 #378 实现收敛或明确隔离。
- `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:632` 仍有本地
  `maxQueuedTurns`，并在 `:943-1016` 参与并发 permit；容量 claim/release 和容量视图应
  移交 #382，#378 只保留 Session 串行与生命周期事实。
- Server 的缺失恢复入口在
  `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:236-261`，
  presence guard 在 `:1104-1130`，断线只在 `:1961-1968` 把 Active 标为 Unknown；这些
  路径尚未覆盖本文统一的 confirmed-missing、recovery window、Unknown Turn 和恢复失败
  next action。当前部分入口仍要求用户先 Reset。
- Runner 已有可复用的事实分类：
  `packages/runner/src/runtime/binding-recovery.ts:48-75` 只对确定的 `missing-session`
  创建空 Session，其它失败保留 binding；`packages/runner/src/server/followup-handler.ts:156`
  及 `packages/runner/src/runtime/binding-convergence.ts:67-110` 已有 follow-up/reconnect
  拼接，但 Server canonical 状态和跨入口原子映射仍需按本文补齐。
- Compact/Reset 当前实现集中在
  `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:264-320`，仍以
  当前 Runtime 存在和 idle 条件为主要入口门槛；需要补齐本文的 context boundary、旧
  transcript 保留和恢复窗口语义。默认 transcript 公共投影属于 #384，历史/Session
  timeline 属于 #385，本 issue 不在这些差距中实现替代方案。
- #484 已落地扁平 transcript 与独立 activity，但退役 `session.closed` 名称仍存在于
  Server/Web 的兼容处理；该清理由既有后续工作负责，#378 不以重做 transcript 投影为
  交付内容。
- 命名 Agent 的 launch 与 `mohist/agent` attempt 已按 Agent 定义固定 Instructions、
  Runtime、Model、Variant 和 Skills；客户端 task/context 输入不能覆盖这些字段。Agent
  配置与启动产品体验仍属于 #377，不是本设计的实现范围。
