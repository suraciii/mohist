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
| 启动 Mohist Agent | AgentJob | AgentJob executor | Agent launch |

```text
Workflow: TaskRun -> Runtime Action adapter --+
                                             +-> Runtime adapter -> Runtime Session
Agent: Mohist Agent -> AgentJob executor -----+
```

两条路径共享 Runner 执行能力和 Session 基础设施，但不共享工作所有者：TaskRun 对
Workflow 工作负责，AgentJob 对 Mohist Agent 工作负责。每个入口把已经解析好的
AgentSession 目标交给 Runtime adapter，Runtime 事实写回该 Session。共享 Runtime
代码不能制造 Workflow -> Agent 的领域依赖。

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

## 工作生命周期与会话

TaskRun 与 AgentJob 拥有以下决策：

- pending / running / terminal 状态；
- 成功、失败与结果；
- retry、recovery 或 Workflow 推进。

AgentSession 拥有以下事实：

- 按顺序记录的输入、回复、tool calls 与 Runtime 状态；
- context 与 usage；
- model / Runtime observations；
- 当前 activity 与 Runtime binding。

Workflow Action adapter 向 TaskRun 报告工作结果，AgentJob executor 向 AgentJob 报告
工作结果；两者都向 AgentSession 报告会话事实。AgentSession 事件不会推进 Workflow，
也不会让 AgentJob 进入终态。工作失败可以成为 transcript 中的诊断，但 Session 不是
工作结果的裁判。

Session 命令不是工作 dispatch。Follow-up 只向现有 AgentSession 追加输入，不创建
TaskRun 或 AgentJob。Compact 与 Reset 同样只改变 Session；它们不轮换 AgentSession ID。

## AgentSession 模型

AgentSession 的结构靠近 Runtime 的物理会话，但拥有 Mohist 的稳定身份：

```text
AgentSession
  Id
  Source
  WorkDir
  Activity
  CurrentBinding?
  Transcript
  Context
  Usage

RuntimeBinding
  RunnerId
  Runtime
  RuntimeSessionId
```

以下是不变量：

- `Id`、`Source` 与 `WorkDir` 在 AgentSession 生命周期内不变。
- `CurrentBinding` 是当前路由事实，可以整体替换；AgentSession 不保存物理 Session 历史。
- `Transcript` 是一个按 AgentSession 顺序追加的会话记录，不按物理 Session 或其它
  子实体拆分。
- `Context` 描述当前 Runtime Session 的上下文；binding 替换后从空开始。`Transcript`
  与累计 `Usage` 不随 binding 替换清空。
- 同一 AgentSession 同时最多有一次 Runtime 执行；该串行约束使 transcript 的 Session
  内顺序足以表达会话。
- AgentSession 没有 `completed`、`failed`、`stopped` 或 `closed` 生命周期。

`CurrentBinding` 允许初始为空。首次执行先创建物理 Session，再把 binding 持久化；只有
binding 持久化成功后才能提交输入。

## Activity 与 transcript

### Activity

AgentSession 的活动状态只有：

| 值 | 含义 |
|---|---|
| `idle` | 没有确认仍在执行的输入，可以开始新的执行、Compact 或 Reset |
| `active` | Runtime 正在处理输入；Follow-up 可以进入当前执行，Cancel 可以尝试停止 |
| `unknown` | 无法确认输入是否已被接受或执行是否已停止；不得当作安全空闲 |

新 AgentSession 的初始 activity 是 `idle`，此时允许 `CurrentBinding` 为空。

状态转换为：

```text
idle + input accepted                 -> active
active + follow-up accepted           -> active
active + execution confirmed stopped  -> idle
active + stop result uncertain        -> unknown
idle + input acceptance uncertain     -> unknown
unknown + runtime evidence             -> active | idle
```

一次执行完成、失败或取消只让 activity 回到 `idle`。具体结果由 TaskRun、AgentJob 或
transcript 中的 Runtime 诊断表达，不能成为 AgentSession 终态。Runtime 进程退出、缓存
回收或持久化文件保留同样不能推导 Session 已关闭。

### Transcript 契约

Transcript 是扁平、按 Session 排序的会话事实。它不为单次输入建立子实体、额外身份、
分组存储或物理 Session 历史。

`session.input` 是输入已被 Mohist 接受的规范事实：

```json
{
  "type": "session.input",
  "payload": {
    "text": "...",
    "source": "task-run | agent-job | followup | system",
    "acceptedAt": "2026-07-22T10:00:00Z"
  }
}
```

每条被接受的输入各有一个 `session.input`。它是 transcript 中的输入边界，不创建另一个
领域资源。消息、reasoning、tool、usage、model、provider retry、compaction 和 status
事实按发生顺序追加在同一 transcript 中。

`session.activity` 是 activity 变化的规范事实：

```json
{
  "type": "session.activity",
  "payload": {
    "activity": "idle | active | unknown",
    "observedAt": "2026-07-22T10:02:00Z"
  }
}
```

`session.input` 与所需的 `idle -> active` 转换由 Session 原子接受；执行中的 Follow-up
只追加输入，activity 保持 `active`。执行结束或状态核对只报告新的 activity，不重复表达
TaskRun 或 AgentJob 结果。所有时间使用 UTC ISO 8601。

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
`session.followup_completed` / `session.followup_failed` 同样不属于目标 DSL：Follow-up
的受理结果不等于 agent 执行结果。

消费者不能从历史错误、完成或停止事实推导当前 activity。当前 activity 由 Session
状态和最新 Runtime 证据决定。

## Follow-up 与 Cancel

Follow-up 的目标由 AgentSession 当前 activity 决定：

| Activity | 行为 |
|---|---|
| `idle` | 向当前 Runtime Session 提交输入并开始执行 |
| `active` | 通过 Runtime 原生 steer / async prompt 能力加入当前执行 |
| `unknown` | 拒绝新输入，先核对当前执行状态 |

Follow-up 命令只需要三种结果：

- `accepted`：Runtime 已确认接受输入；
- `rejected`：Runtime 已确认没有接受输入；
- `unknown`：无法确认是否接受，不能自动重新发送。

`operationId` 可以作为命令幂等和状态核对键，但不是 AgentSession 内的新实体，也不用于
把 transcript 分组。`unknown` 后只能使用同一 operation 核对结果；创建新 operation
重新发送可能产生重复副作用。

Cancel 只针对当前 binding 上唯一可能执行中的操作，不需要额外的执行身份。Runtime 无法确认
已经停止时，Session 进入 `unknown`；停止请求本身不能伪造 idle。

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
- Session 拥有 AgentSession 身份、source、workDir、activity、current binding、transcript、
  context 与 usage。
- Runner 执行已经解析的工作，创建或恢复 Runtime Session，并报告物理事实。
- Runtime adapter 隐藏 SDK / protocol、缓存、进程、文件、事件核对和错误分类。
- Web 和 CLI 只消费 Server 给出的 activity、current binding 与 transcript，不自行从
  历史结果推导 Session 状态。

Server 是 binding 与 activity 的唯一状态裁判。Runner 不能自行决定 current binding
已经改变，也不能因为 Runtime 进程退出就关闭 AgentSession。

## 测试边界

默认测试不访问真实 Runtime、网络、进程、文件系统 Session 或墙钟。至少覆盖：

- 同一 AgentSession 跨 task、retry、Follow-up 与 Runner 重启复用 current binding；
- `session.input` 和 Runtime 事实按 AgentSession 顺序持久化，不要求额外的执行身份；
- binding 替换与 `session.context_reset` 原子持久化，且事件不包含物理 Session 沿革；
- 一次执行完成、失败或取消后 activity 回到 `idle`，没有 Session 终态；
- 停止或输入受理不确定时进入 `unknown`，不会自动重放；
- Reset、Runtime 变化和 confirmed missing 在 `idle` 时原子替换 current binding；
- stale expected binding 与旧 Runtime Session 事件被拒绝；
- binding 替换不创建新 AgentSession、不保存物理 Session history，也不复制物理会话数据；
- TaskRun / AgentJob 结果与 AgentSession activity 互不覆盖。

## 实装差距

#484 已落地扁平 transcript 与独立 activity：不再写入 `session.closed` /
`session.followup_*`，状态与命令资格只读当前 activity；终态以 `session.activity`
（activity=idle + 终态 status）持久化。

残留差距：退役词汇仍留在三条路径上——server 仍接受（但已无生产者）`session.closed`
runtime 事件并映射为同名片段类型；事件 feed 仍把终态 `session.activity` 记录以
`session.closed` 类型展示；web 仍保留该名字的标签与视图处理。#496 负责清除。

Runtime Session 确认缺失后的自动创建与 expected-binding replacement 尚未覆盖全部入口；
当前部分路径仍要求用户 Reset。该差距按本文的最小 current-binding 模型实施。
