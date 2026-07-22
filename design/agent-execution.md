# Agent 执行模型

本文定义 Workflow、Agent、Session、Runner 与 Runtime adapter 共享的抽象边界。
Runtime 特有行为放在 [`runtimes/`](runtimes/README.md)，例如
[`runtimes/opencode.md`](runtimes/opencode.md)。

## 层次

| 层次 | 概念 | 所有者 | 权威状态 |
|---|---|---|---|
| 定义 | Mohist Agent | Agent context | 身份、instructions、config、skills、状态 |
| 工作 | TaskRun | Workflow context | Workflow task 生命周期、结果、输出、恢复 |
| 工作 | AgentJob | Agent context | 一次 Mohist Agent 执行的生命周期与结果 |
| 执行契约 | Action | Workflow context | 一次工作 dispatch 的 `uses` / `with` 输入输出契约 |
| 对话 | AgentSession | Session context | transcript、context、usage、Runtime binding、lineage |
| 对话 | Turn | Session context | 一次对话执行的输入、活动状态与结束结果 |
| Runtime | Runtime Session | 外部 Runtime | 物理对话与 provider 执行状态 |
| Adapter | OpenCodeRuntime、PiRuntime | Runner 进程 | protocol、进程、事件、状态核对、错误 |

`Inline Agent` 是产品使用方式，不是另一个实体或 bounded context。它表示 Workflow
TaskRun 直接选择 Runtime 特有的 Action 并提供输入，不解析 Mohist Agent。

## 规范术语

跨上下文统一定义见 [`../CONTEXT.md`](../CONTEXT.md)。本文只定义这些概念的生命周期、
所有权、事件契约和模块边界，不建立第二套术语。

## 调用路径

| 路径 | 工作所有者 | Runner 入口 | AgentSession 来源 |
|---|---|---|---|
| Workflow 直接调用 | TaskRun | `mohist/opencode` Action adapter | Workflow |
| 启动 Mohist Agent | AgentJob | AgentJob executor | Agent launch |

```text
Workflow: TaskRun -> mohist/opencode Action adapter --+
                                                       +-> OpenCodeRuntime -> Runtime Session
Agent: Mohist Agent -> AgentJob -> AgentJob executor --+
```

两条路径共享 Runner 执行能力和 Session 基础设施，但不共享工作所有者：TaskRun 对
Workflow 工作负责，AgentJob 对 Mohist Agent 工作负责。每个入口把已经解析好的
AgentSession 目标交给 `OpenCodeRuntime`，Runtime 事实写回该 Session。共享 Runtime
代码不能制造 Workflow -> Agent 的领域依赖。

## Action 语义

`mohist/opencode` 是 Runtime 特有的 Action，回答“用 OpenCode 执行这个回合”。它不接收
Agent ID，不解析 Agent 名称，不读取 Agent 定义，也不创建 AgentJob。因此 Workflow
直接使用它时形成 Inline Agent。

`mohist/pi` 等 Runtime Action 与它处于同一层（见 [`runtimes/pi.md`](runtimes/pi.md)）。
本设计有意不定义
`mohist/agent` 契约；该名称留给后续 Mohist Agent 专项设计，不能在这里充当 Runtime
别名或 `mohist/opencode` 的通用包装。

AgentJob 路径不能通过公开的 `mohist/opencode` Action 契约 dispatch。Agent 定义完成
解析和 snapshot 后，其 executor 接收由 Agent 拥有的 execution request。Workflow Action
adapter 与 AgentJob executor 都可以调用同一个 `OpenCodeRuntime` 深模块。复用点是
Runtime 实现，不是 Action。

## 工作生命周期与对话

TaskRun 与 AgentJob 拥有以下决策：

- pending / running / terminal 状态；
- 成功、失败与结果；
- retry、recovery 或 Workflow 推进。

AgentSession 拥有以下事实：

- Turn、用户 / agent 消息和 tool calls；
- context 与 usage；
- model / Runtime observations；
- 当前 Runtime Session 绑定与会话沿革（lineage）。

Workflow Action adapter 向 TaskRun 报告工作结果，AgentJob executor 向 AgentJob 报告
工作结果；两者都向 AgentSession 报告 Runtime 事实。AgentSession 事件不会推进 Workflow，
也不会让 AgentJob 进入终态。失败的 AgentSession 操作可以成为工作所有者判断的证据，
但 Session 不是裁判。

Session 命令不是工作 dispatch。执行中提交的 Follow-up 成为当前回合输入；空闲时提交
Follow-up 会启动一个用户发起的对话回合，只记录命令和 Runtime 事实，不创建 TaskRun
或 AgentJob。Compact 与 Reset 遵循相同的 Session-only 所有权规则，且都只在逻辑
Session 空闲时执行；两者都不轮换 AgentSession ID，命令响应返回同一稳定
`sessionId`。在用户显式 Session 命令中只有 Reset 替换 Runtime 绑定；新 Turn 提交前的
缺失恢复属于下文独立的 binding 准备，不是另一个 Session 命令。

## Turn 生命周期与 transcript DSL

Turn 是 AgentSession 内唯一拥有对话执行终态的实体。AgentSession 可以先后包含多个
Turn；一个 Turn 的完成、失败或停止只结束本次对话执行，不关闭逻辑 Session，也不改变
TaskRun 或 AgentJob 的工作裁决。

### 状态投影

AgentSession 的查询模型把互不替代的状态轴分开呈现：

| 轴 | 值 | 含义 |
|---|---|---|
| `activity` | `idle` / `active` / `unknown` | 当前是否有可确认的执行中 Turn |
| `binding` | `unbound` / `bound` / `missing` | 当前 Runtime Session 绑定是否存在且可解析 |
| `currentTurn` | 无，或当前 Turn 摘要 | `active` / `unknown` 时命令和状态核对的稳定目标 |
| `latestTurn` | 无，或最近 Turn 摘要 | 最近 Turn 的身份、时间与结果，不是 Session 终态 |

Turn 在输入被受理时直接进入 `active`，结束后进入 `finished`。`finished` 必须且只能带一个
`outcome`：`completed`、`failed` 或 `stopped`。AgentSession 没有与普通 Turn 对应的
`completed`、`failed` 或 `closed` 状态，也没有 `closedAt`；需要展示最近结束时间时读取
`latestTurn.finishedAt`。未来若产品引入显式归档，应使用独立的 `archivedAt` 语义。

`activity: unknown` 表示 Mohist 知道当前 Turn 可能仍在 Runtime 执行，但无法确认是否已经
停止，或空闲 Follow-up 可能已经启动新 Turn，但受理结果未知。此时 Follow-up、Compact 与
Reset 都不能假装 Session 已空闲；Cancel 可以继续针对 `currentTurn` 尝试停止或触发状态
核对。只有新的 Runtime 证据完成核对后，activity 才能转为 `active`，或由
`turn.finished` 转为 `idle`。如果最终确认输入从未被受理，则清除候选 `currentTurn` 并
恢复 `idle`。

### 事件契约

以下名称是 transcript DSL 的唯一规范名称。所有 `turn.*` 事实和属于某个 Turn 的
message、tool、usage、model、status、compaction 事实都必须携带稳定的 `turnId`；
`turnId` 在所属 AgentSession 内唯一且在重投时不变。

`turn.started` 记录一次 Turn 以及它的首个输入：

```json
{
  "turnId": "turn_...",
  "startedAt": "2026-07-22T10:00:00Z",
  "originKind": "task-run | agent-job | followup",
  "originId": "...",
  "input": {
    "inputId": "input_...",
    "text": "..."
  }
}
```

`turn.input.added` 记录执行中 Turn 受理的追加输入：

```json
{
  "turnId": "turn_...",
  "inputId": "input_...",
  "addedAt": "2026-07-22T10:01:00Z",
  "source": "followup | system",
  "text": "..."
}
```

`turn.finished` 是 Turn 唯一的结束事实：

```json
{
  "turnId": "turn_...",
  "finishedAt": "2026-07-22T10:02:00Z",
  "outcome": "completed | failed | stopped",
  "failure": {
    "code": "...",
    "message": "...",
    "category": "..."
  }
}
```

`failure` 只允许在 `outcome: failed` 时出现，`category` 可省略。进程退出码、Action
结果和工作裁决属于 TaskRun 或 AgentJob，不进入 Turn 结束事实。

Follow-up 的命令结果与 Turn 事实分开记录：

| 事件 | 必需字段 | 语义 |
|---|---|---|
| `followup.admitted` | `operationId`、`turnId`、`placement`、`admittedAt` | Runtime 已确认受理；`placement` 是 `current-turn` 或 `new-turn` |
| `followup.rejected` | `operationId`、`rejectedAt`、`error.code`、`error.message` | 已确认输入没有被受理，可以向用户确定失败 |
| `followup.delivery.unconfirmed` | `operationId`、`turnId`、`observedAt`、`attemptedPlacement`、`error.code`、`error.message` | 无法确认输入是否已被受理，不能自动重试 |

三个事件的 payload 形状分别为：

```json
{
  "operationId": "followup_...",
  "turnId": "turn_...",
  "placement": "current-turn | new-turn",
  "admittedAt": "2026-07-22T10:03:00Z"
}
```

```json
{
  "operationId": "followup_...",
  "rejectedAt": "2026-07-22T10:03:00Z",
  "error": {
    "code": "...",
    "message": "..."
  }
}
```

```json
{
  "operationId": "followup_...",
  "turnId": "turn_...",
  "attemptedPlacement": "current-turn | new-turn",
  "observedAt": "2026-07-22T10:03:00Z",
  "error": {
    "code": "...",
    "message": "..."
  }
}
```

所有 `*At` 字段都是 UTC ISO 8601 时间。`error.code` 是稳定机器码，`error.message` 是
保留原始原因的用户可读诊断。

Mohist 在投递前为 Follow-up 分配 `operationId`，并为 `new-turn` placement 预分配
`turnId`；同一 operation 的状态核对和重投查询复用这些身份。Runtime 确认空闲 Follow-up
后，按 `followup.admitted`、`turn.started` 的顺序在同一次持久化提交中记录；确认执行中
Follow-up 后，按 `followup.admitted`、`turn.input.added` 的顺序同次记录。Rejected 或
delivery-unconfirmed 不生成 Turn 输入事实。

`followup.admitted` 与 `followup.rejected` 是互斥的最终受理结果；同一 operation 的最终
结果只能出现其中一个。`followup.delivery.unconfirmed` 是可被后续核对收敛的中间事实，
不授权重新发送输入；调用方用原 `operationId` 查询结果，不能创建一次新的副作用。对
`new-turn` 投递，未确认事实中的预分配 `turnId` 成为候选 `currentTurn`，并把 activity
投影为 `unknown`；后续证据确认受理后才补记 admitted 与 started，确认未受理后则记
rejected 并恢复 `idle`。对
`current-turn` 投递，原 Turn 保持 `active`，直到其执行事实另有变化。

`followup.admitted` 表示输入已进入 Runtime，不表示 agent 已经完成，因此不得使用
`followup_completed` 命名。

### 顺序与结束规则

- 每个 `turnId` 恰好有一个 `turn.started`，最多有一个 `turn.finished`；重复投递只能重放
  同一事实，冲突的终态必须拒绝。
- 同一 Turn 内，输入先于由其产生的输出，`turn.finished` 最后；结束后不能再追加输入或
  Runtime 事实。Transcript part 的 `sequence` 只在同一 Turn 内比较，不得跨 Turn 排序。
- Runtime 正常完成、确认失败或确认停止后才能记录 `turn.finished`。停止请求或超时若未
  确认，必须把 activity 投影为 `unknown`，不得伪造 `stopped` 或 `failed` 结束事实。
- `turn.finished` 把 AgentSession activity 投影回 `idle`，但不清除 Runtime binding，
  不妨碍后续 Follow-up 开始新 Turn。
- `canFollowup` 由 activity、binding 与 Runner 可用性推导；`canCancel` 只由当前 Turn
  推导。历史 Turn 的结果不能禁用未来命令。

Runtime Session 的内存缓存、进程资源释放、持久化文件保留与淘汰策略属于 Runtime
adapter。它们不能通过 transcript 事件表达，也不能用来推导 AgentSession 已关闭。

## AgentSession 来源

每个 AgentSession 有且只有一个不可变来源。

### Workflow 来源

使用 `(projectId, workflowRunId, sessionName)` 寻址。同一 WorkflowRun 内复用相同名称
会继续逻辑对话。省略显式名称时使用 Work ID，避免无关 task 意外共享 context。

### Agent launch 来源

每次启动 Mohist Agent 时创建，并关联已解析的 Agent ID。一个 Mohist Agent 可以创建
多个 AgentJob 和 AgentSession。之后编辑或归档 Agent，不改变 Session 来源或启动时的
执行 snapshot。

相同 prompt、model、Runtime、workspace 或配置不会合并两个来源。Session 不能从
Workflow 来源迁移为 Agent 来源，反之亦然。

来源特有的 route 只是查询和便利入口，最终都解析为以 `sessionId` 标识的规范
AgentSession 资源；`(workflowRunId, sessionName)` 和 `agentId` 都不能替代 Session 身份。

Follow-up、Compact、Reset、transcript 与查询都作用于该规范资源。来源特有的 CLI 或
API 可以先解析它，但不能实现第二套 Session 生命周期。

## 逻辑与物理 Session 身份

AgentSession ID 是逻辑对话的稳定身份。Runtime Session 身份是外部物理维度：

```json
{
  "runtime": "opencode",
  "runtimeSessionId": "ses_..."
}
```

Runtime 变化、Reset 和已确认的 Runtime Session 缺失恢复可以替换物理绑定并追加
lineage，但不能改变 AgentSession 身份或来源。工作目录是逻辑 AgentSession 的不可变
属性；变化时必须使用新的逻辑 Session 身份，不能替换原 Session 的物理绑定。Compact
和 model / variant 选择变化不会替换物理绑定。

持久化的当前绑定只保留 Runner 重启后继续控制所需的最小数据：`runtime`、
`runtimeSessionId`、`runnerId` 与 `workDir`。Lineage 记录 `runtime`、
`runtimeSessionId`、`boundAt` 与 `reason`。`reason` 只有 `initial`、`reset`、
`runtime-change` 和 `missing-recovery`；它让查询与 UI 明确区分主动清空上下文、后端切换
和物理资源丢失，不能用自由文本代替。

## Runtime Session 缺失恢复

缺失恢复是 Session 对 Runtime binding 的修复，不是 Prompt retry，也不是 Workflow
recovery。它只在一个新 Turn 的输入尚未被 Runtime 接受时发生。成功修复后，同一个工作
attempt 继续执行，不消耗 Workflow recovery budget。

### 触发条件

以下条件必须同时成立：

1. AgentSession 可以受理新 Turn，`activity` 为 `idle`；
2. 本次执行位于当前绑定的 `runnerId`，绑定的 Runtime 与本次执行要求一致，`workDir` 与
   AgentSession 的不可变工作目录一致；
3. 绑定所属 Runner 上的 Runtime adapter 用确定性证据确认当前 `runtimeSessionId` 已不存在；
4. 本次输入尚未写入 transcript，也尚未向 Runtime 提交；
5. binding 替换与输入记录各自提交时，Server 看到的 expected binding 仍是 current。

Runtime 不可用、超时、权限失败、响应形状不兼容、数据损坏或任何无法区分“暂时无法
读取”和“确定不存在”的结果都不满足条件。执行请求落在另一个 Runner 也不是 missing：
它必须路由回绑定所属 Runner 或明确失败，不能借缺失恢复迁移物理 Session。上述情况都
保留原 binding 并形成可行动失败。

`ready`、`definitely-missing` 与失败是 Mohist 共享的语义结果，不是一个泄漏 SDK 类型的
通用 Runtime 接口。OpenCodeRuntime 与 PiRuntime 各自在自己的深模块中产生这些结果；
Runner 的 binding 准备只编排共同顺序，不能把 provider 判定上移到 Session 或 Workflow。

### 解析与替换顺序

新 Turn 的 binding 准备只有一条权威流程：

```text
expected = AgentSession.currentRuntimeBinding

if expected is absent:
    candidate = Runtime.create(requiredRuntime, immutableWorkDir, current turn options)
    selected = Session.replaceRuntimeBinding(
        expected = absent,
        candidate = candidate on currentRunnerId,
        reason = initial)
else:
    require currentRunnerId == expected.runnerId
    resolved = Runtime.resolve(expected)

    if resolved is ready:
        selected = expected
    else if resolved is definitely-missing:
        candidate = Runtime.create(expected.runtime, expected.workDir, current turn options)
        selected = Session.replaceRuntimeBinding(
            expected,
            candidate,
            reason = missing-recovery)
    else:
        fail without changing the binding

Session.recordInput(expectedBinding = selected, input)
submit the input exactly once
```

自动恢复最多创建一个 candidate；新 candidate 创建或 binding 持久化失败时，不提交输入。
已经创建但未能绑定的 candidate 不得用于执行；它只形成诊断，不要求在本流程内同步删除，
也不引入影响 binding 裁决的补偿协议。

`Session.replaceRuntimeBinding` 是 Server 中的状态裁决。命令必须携带 expected
binding 的 `runnerId`、`runtime`、`runtimeSessionId` 与 `workDir`；Session 只在 expected
仍完整等于 current、activity 仍允许新 Turn 时原子替换 binding 并追加 lineage。
`missing-recovery` 建立的 replacement 保持同一 `runnerId`、Runtime 与工作目录，只替换
`runtimeSessionId`。过期结果必须拒绝，不能覆盖 Reset、Runtime 切换或另一轮恢复已经建立
的 binding。Runner 只报告 Runtime 的 missing/create 事实，不能自行宣称 Server binding
已经改变。

`Session.recordInput` 再次携带 selected 完整 binding。Server 只在它仍是 current 且
AgentSession 仍可受理新 Turn 时，原子记录输入并建立 Turn；Reset、Runtime change 或另一
轮恢复若已先改变 binding，本次记录必须失败。Runner 只有收到输入持久化确认后才能提交
Prompt，因此换绑与输入之间不需要跨网络锁或新的持久化恢复 Job。

### 操作边界

| 操作 | 确认缺失时自动替换 | 原因 |
|---|---:|---|
| TaskRun 或 AgentJob 开始新 Turn | 是 | 输入尚未提交，工作可以在空上下文继续 |
| AgentSession 空闲时的 Follow-up | 是 | 它将开始新 Turn，使用同一 admission 与提交顺序 |
| 执行中 Turn 的 Follow-up | 否 | 输入目标是当前 Turn，替换后语义不再相同 |
| Compact | 否 | 缺失的上下文无法压缩 |
| Cancel | 否 | 新 Session 不是原执行中的 Turn |
| Reset | 不属于自动恢复 | 用户主动创建空 Session；旧 Session 缺失也不能阻止 Reset |

自动恢复建立空上下文，不从 Mohist transcript 重放消息、Prompt 或 tool call。重放会把只读
审计记录变成新的执行输入，并可能重复外部副作用。Lineage 的 `missing-recovery` 是上下文
中断的持久事实；产品查询和 UI 必须展示它，不能把替换后的物理对话表示为无缝连续。

一旦 Prompt 提交已经开始，任何 missing、transport failure 或响应不确定都不得进入上述
流程。当前 Turn 按原错误结束或进入 `unknown`，工作所有者决定后续 retry；Runner 不能
创建新 physical Session 后自动重放 Prompt。

本能力不增加 Workflow DSL、Action Input、Agent 配置开关、恢复 Stage 或持久化恢复 Job。
一次 binding 准备内的单次 candidate 创建与 expected-binding 裁决已经完整表达恢复过程。

### 验证责任

- Session domain spec 覆盖 initial、missing-recovery、Reset 与 Runtime change 四种
  lineage reason，以及 expected binding 不匹配、activity 非 idle、Runner / Runtime /
  workDir 不匹配时的拒绝。
- Runner spec 从 Workflow、AgentJob 与 idle Follow-up 三个产品入口证明：binding 先于
  input、input 先于 Prompt；恢复成功只提交一次，重新绑定或 input CAS 失败不提交。
- 路由 spec 证明新 Turn 只交给 binding 的 `runnerId`；其它 Runner 不能把本地 404 或文件
  不存在报告成该 binding 的 `definitely-missing`。
- Runtime unit test 只验证本后端的 `ready` / `definitely-missing` / ambiguous 分类和
  candidate 创建；Server 状态矩阵不通过 Runtime test 重复。
- 所有测试使用 fake Runtime、fake Server connection 与可控信号，不访问真实进程、网络、
  文件系统或墙钟。

## Mohist Agent 启动

Agent context 负责组装启动请求：

1. 按 ID 或名称解析 active Mohist Agent；
2. 把 Agent ID、instructions、config 与 launch prompt 固定到 AgentJob input；
3. 创建并打开 Agent launch 来源的 AgentSession；
4. 把 AgentJob dispatch 给合适的 Runner；
5. Runtime executor 只处理已经组装好的回合输入与 Session 绑定。

Runtime adapter 不再查询 Agent 定义。这样并发修改 Agent 不会改变执行中的输入字节，
Runtime 模块也不依赖 Agent context。

## 模块边界

- Workflow 拥有 TaskRun 与 `uses` / `with` Action 契约。
- Agent 拥有 Mohist Agent、AgentJob、启动组装、AgentJob execution request 与报告校验。
- Session 拥有 AgentSession 身份、metadata、transcript、usage 与 lineage。
- Runner context 只记录执行资源是否在线及其容量，不拥有 Agent 或 Session 语义。
- Runner 进程执行 dispatch 并适配外部 Runtime，不拥有业务实体。

Runtime adapter 接收由 Mohist 定义的回合 / Session 请求并返回规范化事实。它不能
暴露 SDK 类型、解析 Agent 定义、决定 Workflow transition 或拥有 job status。

## 不变量

- Action 不是 Agent。
- AgentSession 不是 Agent，也不是工作所有者。
- Turn 结果不终结 AgentSession；工作结果、Turn 结果、Session activity 与 Runtime
  资源生命周期是四个独立维度。
- 所有 Turn 范围内的 transcript 事实都携带稳定 `turnId`，不得跨 Turn 解释局部顺序。
- Inline Agent 没有 Agent ID 或可复用定义。
- Mohist Agent 有稳定身份，可以拥有多次执行和多个 Session。
- 一次 dispatch 的工作所有者只能是 TaskRun 或 AgentJob 之一。
- 每个 AgentSession 只有一个不可变来源。
- 替换 Runtime Session 不改变 AgentSession 来源或逻辑身份。
- 明确缺失的 Runtime Session 只可在新 Turn 输入提交前，以 expected binding 原子替换。
- Runtime Session 缺失恢复不重放 Prompt，不消耗 Workflow recovery budget。
- `mohist/opencode` 不暴露 OpenCode 原生 agent 选择。
- AgentJob 执行不依赖 Workflow Action 名称或 Action Input 契约。
- 共享 `OpenCodeRuntime` 不制造 Workflow -> Agent context 依赖。

## Status

本文以上内容是目标设计。当前实现仍写入或消费 `session.input`、`session.closed`、
`session.followup_completed` 与 `session.followup_failed`，并有消费者从历史 close 事实
推导整个 AgentSession 的状态和命令能力；部分 transcript part 的局部序列也尚未以
`turnId` 分区。对应实施 issue 待从本 spec 创建。

Runtime Session 缺失恢复也尚未完整实现。当前部分 Runtime 路径仍把确定 missing 直接
返回给工作所有者，并要求用户 Reset；lineage 尚未记录 replacement reason。对应实施
issue 待从本 spec 创建。

迁移不保留 `session.closed` 别名或新旧事件双写。实施必须一次处理当前读写路径、已持久化
transcript 与待投递 outbox，使同一 AgentSession 不会同时存在两套相互冲突的终态语义。
