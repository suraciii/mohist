# Agent 与 AgentSession

Mohist Agent 是 Project 内可独立配置和使用的 Agent。用户可以在 Web UI 或 CLI 中直接
启动它，也可以把同一个 Agent 接入 Slack，或让它响应事件与评论提及。入口可以变化，
Agent 的身份、Instructions、执行配置、Skills、AgentJob 和 AgentSession 不变。

第三方的外部 Agent 是另一条路径：它通过 Mohist Skill 和 `mo` 查询、委托或操作执行层，
不是 Mohist 资源。只有当它显式启动一个 Mohist Agent 时，才会产生该 Mohist Agent 的
AgentJob 与 AgentSession。完整产品边界见[核心概念](concepts.md)。

## 产品承诺

- **Agent 先独立可用**：没有 Slack 等外部接入时，用户也能完整配置、启动、继续对话、
  读取结果和处理异常。
- **配置只有一份**：Instructions、执行后端、模型、Variant、Skills 和并发限制由 Mohist
  Agent 拥有；名称、头像和描述也构成同一个 Agent 身份。Web、CLI 和 Agent 接入不能
  保存或覆盖另一份定义。
- **入口不改变语义**：一次新的委托创建 AgentJob、AgentSession、首条 SessionInput 和首个
  AgentTurn；对已有会话继续输入会创建新的 SessionInput，但不创建第二个 AgentJob。
- **执行状态可追溯**：AgentJob 回答首次 launch 是否成功，AgentSession 回答发生了什么、
  每次后续输入的结果以及当前能否继续。Slack 消息或 Web 页面都不是状态裁判。

## 概念层次

| 概念 | 是什么 | 身份和生命周期 |
|---|---|---|
| Inline Agent | Workflow 直接配置并调用 Agent 能力的用法 | 不是资源，没有 Agent ID；配置随 task 输入存在 |
| Agent 定义引用 | Workflow task 用 `uses: mohist/agent` 引用 Mohist Agent 定义的用法 | 不是资源，没有 Agent ID；定义在 task 开始执行时固定 |
| Mohist Agent | Project 内预先定义、按名称复用的 Agent 资源 | 有稳定 Agent ID、名称、指令、配置、Skills 和状态 |
| Agent 接入 | 把一个 Mohist Agent 暴露到 Slack 等外部交互场所 | 有独立连接生命周期；只引用 Agent，不拥有或复制 Agent 配置 |
| AgentJob | Mohist Agent 的一次 launch 执行 | 独立记录等待、执行、完成或失败，以及首次执行结果 |
| SessionInput | AgentSession 接受的一条输入 | 有稳定 Input ID；记录内容、附件、来源、顺序和投递状态，一个 Turn 可以处理多条 Input |
| AgentTurn | Runtime 连续处理一组有序 SessionInput 的过程 | 有稳定 Turn ID 和状态；由 AgentSession 拥有，不是新的顶层工作 |
| AgentSession | Mohist 记录的一段持续会话 | 有稳定 Session ID；按顺序拥有 Input 与 Turn，并保存上下文、用量、活动状态和当前 Runtime Session |
| Runtime Session | OpenCode、Pi 等执行后端实际维护的物理会话 | 由执行后端标识；必要时可以被 AgentSession 替换 |

Action 不在 Agent 资源层：`mohist/opencode` 描述一次工作如何交给 OpenCode，
不代表一个有身份的 Agent。

## 两条调用路径

| 使用路径 | 是否有 Agent 身份 | 谁负责本次工作 | 如何执行 | AgentSession 来源 |
|---|---|---|---|---|
| Workflow 直接调用 | 否（Inline Agent 或 Agent 定义引用） | TaskRun | 执行后端 Action（`mohist/opencode`、`mohist/pi`）或 `mohist/agent` | Workflow |
| 启动 Mohist Agent | 是；使用已保存的 Mohist Agent | AgentJob | Mohist Agent 的内部执行入口 | Agent launch |

两条路径可以使用同一种执行后端能力和同一种 AgentSession 模型，但不会共享 Agent
身份或工作生命周期。Workflow 通过执行后端 Action 调用 OpenCode 或 Pi；Mohist Agent
由 AgentJob 执行，只在底层复用执行后端能力，并不反过来调用 Workflow Action。

## Inline Agent

Inline Agent 是一种使用方式，不是持久化实体。Workflow task 直接声明：

- 用哪个执行后端 Action，例如 `mohist/opencode`；
- 这次执行的 prompt；
- 可选的 Session 名称和模型选项。

它适合 Workflow 中的规划、实现、审查和修复。它没有名称、Instructions、Skills
或 Agent ID，不能被事件路由规则引用，也不能被 `mo agent` 命令查找。

Workflow TaskRun 拥有这次 task 的成功、失败和输出。Action 是执行接口，AgentSession
只保存会话内容和执行事实。

## Agent 定义引用

task 也可以改用 `uses: mohist/agent` 并给出 `name`，引用一个预定义 Mohist Agent
的指令与执行配置来完成本次执行。这不是 Inline Agent（指令与配置不随 task 输入
存在，来自 Agent 资源），也不是启动 Mohist Agent（不创建 AgentJob）：TaskRun
拥有成败，AgentSession 仍是 Workflow 来源。契约见
[`mohist/agent` Action](actions/agent.md)。

## Mohist Agent

Mohist Agent 是 Project 内的一等资源。它保存：

- 稳定 ID，以及名称、头像和描述组成的可识别身份；
- Instructions 和 Agent 配置；
- Skills；
- 并发限制与 active / archived 状态。

## 配置一个 Agent

| 配置 | 用户需要回答的问题 | 生效规则 |
|---|---|---|
| 名称 | 在 Project 和外部场所中如何识别它 | Project 内唯一；重命名不改变 Agent ID |
| 头像 | 在 Web、Slack 和执行记录中如何快速识别它 | 立即更新 Mohist 展示，并同步到支持更新的接入 |
| 描述 | 什么时候应该选择它 | 只用于发现和选择，不进入执行指令 |
| Instructions | 它扮演什么角色、如何工作、何时停手 | 每次新 AgentJob 启动时固定 |
| Runtime | 由哪个执行后端运行 | Agent 自己拥有；普通客户端不能临时覆盖 |
| Model / Variant | 使用哪个模型和推理档位 | Agent 自己拥有；未配置时使用该 Runtime 的默认值 |
| Skills | 启动时加载哪些能力说明 | 随 AgentJob 固定；入口不能临时增删 |
| Max concurrent runs | 该 Agent 最多同时运行多少次执行，包括 launch 与 follow-up | 实时用于后续调度；调低时不强停已在运行的执行，超出后排队 |
| 状态 | 是否允许接受新委托 | archived Agent 拒绝新委托，已有 Session 仍可查看和继续 |

模型供应商与 Runtime 凭据在受保护的 Runtime 设置中配置，不写进 Instructions，也不复制到
Agent 或 Agent 接入。Agent 只引用 Runtime、Model 与 Variant；Readiness 汇总当前引用是否
真的可执行，并把缺失凭据指向唯一的设置入口。

本次委托可以附带 Issue、Epic、Repository 等上下文引用，但上下文不是 Agent 配置。普通
客户端只能提供任务文本和上下文，不能覆盖这些执行定义或并发限制。Agent 的定义在一次
launch 或 Workflow Agent task attempt 开始时固定；该次执行加载的 Skills 也随之固定。
这样，从 Web 测试通过的 Agent 接入 Slack 后仍然是同一个 Agent。

名称、头像和描述是展示身份，编辑后立即用于 Mohist 的发现与展示；Agent 接入异步同步
支持更新的外部身份，并明确显示未同步状态。Instructions、Runtime、Model、Variant 和
Skills 是执行定义，只影响之后创建的 AgentJob。每个 AgentJob 保存启动时的执行快照；已有
AgentSession 的后续输入继续使用该会话建立时的配置与上下文，不因 Agent 被编辑而悄悄换
模型或能力。Max concurrent runs 是 Agent 当前的调度策略，所有 Session 的下一次执行都按
最新值排队；修改它不改变任何 Session 的执行定义。

Workflow 的 `mohist/agent` task 也会在每次 attempt 开始时固定完整 Agent 定义。编辑 Agent
不会改变已经 dispatch 的 attempt；retry 会重新读取当时的定义，因此修复 Runtime、Model、
Variant、Instructions 或 Skills 后，新的 retry 才会采用修改。

## Readiness 与可用性

Agent 的 active / archived 只回答“是否接受新委托”。Readiness 回答 Mohist 当前能否确认
Agent 的执行配置完整：

| Readiness | 含义 | 用户动作 |
|---|---|---|
| Ready | Mohist 已确认当前定义可以执行 | 可以测试或启动 |
| Needs setup | Mohist 已确认存在配置缺口 | 阻止启动，并列出缺口和修复入口 |
| Unknown | Mohist 暂时无法确认是否可执行 | 可以提交并等待验证，但不能宣称已经可用 |

Runner 暂时离线或没有空闲容量属于 Availability，不把 Ready Agent 改成 Needs setup；工作
可以被接受并排队。Web、CLI 和 Agent 接入只呈现 Mohist 给出的统一结论，不各自维护一套
Runtime 判断规则。

Availability 说明现在能否开始一项新的执行。已经排队的 AgentJob 在 Runner 或容量恢复后，可能会
短暂显示为“等待调度”，直到它的下一次调度尝试开始；这不是新的配置缺口，也不表示 Runner 再次离线。

### 在 Web UI 中配置和测试

1. 在 **Agents** 中创建或打开 Agent，填写名称、头像、描述和 Instructions。
2. 选择 Runtime 后，只展示该 Runtime 真正支持的 Model、Variant 与凭据要求；
   再选择 Skills 和并发限制。页面必须显示 Readiness 和每个缺口。
3. Readiness 为 Ready 后，使用 **Start session** 提交一个真实任务；Unknown 时也可以提交，
   但页面必须说明任务会等待 Runner 验证。创建成功后进入 AgentSession。
4. 在 Session 中查看回复和执行事实，并用 follow-up 验证连续对话。
5. 确认 Agent 能独立完成目标后，再配置事件路由或 Agent 接入。

### 在 CLI 中配置和使用

```bash
mo agent create --name explorer --description "Explore product needs" --instructions "Clarify the request, identify missing decisions, and produce actionable issues." --runtime opencode --skills mohist,mohist-explore --max-concurrent-runs 1
mo agent view explorer
mo agent launch explorer --prompt "探索一个可以从 Slack 调用 Mohist Agent 的产品方案"
# 响应丢失后使用启动前打印的 key 重试，不要生成新的启动
mo agent launch explorer --prompt "探索一个可以从 Slack 调用 Mohist Agent 的产品方案" --idempotency-key <key>
```

`agent view` 显示 Readiness、Availability 与配置缺口；Needs setup 时按提示
补齐再启动。`agent launch` 返回 AgentJob ID、AgentSession ID、首个 Input ID 与 Turn ID。
首次启动的工作结果和 composite observation 用返回的 observation URL 读取；连续对话用
`mo session followup` 提交新的 SessionInput，完整记录用 `mo session transcript`。pending、queued
和 executing 状态继续观察，terminal 状态读取结果或 transcript，Unknown 必须用原 key 重读或重试。
CLI 与 Web 调用的是同一组产品能力。

## 启动入口

| 入口 | 新委托是什么 | Mohist 中发生什么 |
|---|---|---|
| Web UI | 选择 Agent，输入任务和可选上下文 | 创建 AgentJob、AgentSession、首条 Input 和首个 Turn，并进入会话页 |
| CLI | `mo agent launch <agent>` | 创建相同的 AgentJob、AgentSession、首条 Input 和首个 Turn，返回对应 ID |
| Agent 接入 | Slack 私聊中的首项任务、明确的 New task，或一次新的频道根提及 | 接入把消息交给已绑定的 Agent，不改变 Agent 配置 |
| 事件路由 | 规则命中的事件和响应提示词 | 为该事件创建一次 AgentJob 与 AgentSession |
| Issue 评论提及 | `@<agent-name>` 后的评论内容 | 以评论为任务，并关联该 Issue 上下文 |

提及把评论正文作为本次输入，并
自动带上该 issue 的上下文——这是一次性工作，适合「@my-agent 监督并推进这个
issue」这样的当面包办；如果要求的是持续关注，Agent 会自己用 `mo issue watch add` 把这个 issue 加入关注。
无论哪种方式，启动时都会创建
AgentJob，并固定本次使用的 Agent 指令和配置；之后编辑 Agent，不改变已经开始的工作。

Mohist Agent 的核心位置是代理人：它进入流水线上原本由 owner 负责的位置，通过
和人相同的命令与审批通道执行动作。一个 Mohist Agent 可以有多个 AgentJob，也可以
有多个 AgentSession。

Mohist Agent 还可以在自己的会话里 spawn 其它 Agent 的子会话，把运行时才能看清
形状的任务分解出去，形成会话树。见 [Subagent 与会话树](subagents.md)。

把 Agent 接入 Slack 的线程与权限规则见 [Slack](slack.md)。

## AgentJob 与 AgentSession

AgentJob、SessionInput、AgentTurn 和 AgentSession 在 launch 收敛成功后可同时出现，但职责不同：

| | AgentJob | SessionInput | AgentTurn | AgentSession |
|---|---|---|---|---|
| 回答的问题 | 这次 launch 工作成功了吗 | 这条输入是否已接受、排队或交给 Runtime | 这一轮处理到了哪里、结果是什么 | 这段会话发生了什么、现在能否继续输入 |
| 拥有 | launch 调度、成功或失败、工作结果 | 输入内容、顺序、来源和投递状态 | Input 集合、执行状态和对应回复 | Input/Turn 顺序、上下文、用量、活动状态和当前 Runtime Session |
| 生命周期 | 一次 launch 工作，最终完成、拒绝、失败、取消或 blocked | 一条输入先被接受或明确拒绝；accepted 输入的派发仍可能临时 blocked，之后终态为 dispatch terminal | 一次连续执行，最终完成、失败、取消、blocked 或暂时 unknown | 持续存在，可以接受多次输入 |
| 所属概念 | Mohist Agent 的工作 | AgentSession 的子记录 | AgentSession 的子记录 | Session 记录 |

Workflow 的对应工作所有者是 TaskRun，而不是 AgentJob。TaskRun 或 AgentJob 负责裁定
工作结果；AgentSession 只记录执行事实，不推进 Workflow，也不裁定 AgentJob。

首个 AgentTurn 与 AgentJob 关联；后续 AgentTurn 不修改 AgentJob。AgentJob 的 `completed`
表示 launch 工作已由 Runtime 成功处理，不表示整段对话关闭，也不
保证用户口头描述的宽泛目标已经交付。Agent 可以在回复中提出问题；此时 AgentJob 可以已经
完成，AgentSession 回到空闲，用户通过 follow-up 继续。每次 follow-up 创建一个有稳定 ID
的新 SessionInput：空闲时它开始新的 AgentTurn；执行中可由当前 Turn steer 接受，不支持
steer 时则按顺序等待下一 Turn。两种情况都不创建新的 AgentJob，也不重写首次启动的结果。

需要被持续追踪到 Done 的业务工作应由 Agent 创建或推进 Issue / Workflow；不要靠一个永不
结束的聊天 Job 代替执行层。用户要开始另一项需要独立启动记录的工作时，应再次 launch，
从而得到新的 AgentJob 与 AgentSession。

## Session 活动状态

AgentSession 的结构和用户心智模型靠近 OpenCode、Pi 等会话：它持续保存消息，同时
呈现当前是否有尚未完成的 Turn；具体是排队还是执行中由 Turn 状态显示。

SessionInput 和 AgentTurn 都有稳定身份。每条被接受的输入保留一个 Input ID，并恰好关联一个 Turn ID；同一请求的重试不得创建第二组 ID。被 queue full 拒绝的请求没有 live Input/Turn ID，但有持久 request fingerprint、reason 和 nextAction tombstone。普通后续输入使用 `new-turn`，只有 Runtime 明确支持 steer 时，才可使用 `steer` 关联当前 running Turn；已有 Input 的 Turn ID 不可改写。

Input acceptance 是独立事实，取值为 `accepted`、`rejected` 或 `unknown`。队列已满时在受理前持久化 definitive rejection tombstone 后拒绝新的输入；同 requestId 同 fingerprint 的 response-loss retry 永远返回同一 rejection，改 payload 返回 `idempotency_key_reused`，只有新 requestId 才能稍后重试。输入一旦 accepted，即使后续派发失败也不会被删除、换 ID 或改成 rejected。Turn 的执行状态取值为 `queued`、`running`、`outcome_pending`、`terminal` 或 `unknown`；Turn 本身没有 `idle` 状态。

Session activity 取值为 `idle`、`active` 或 `unknown`。只有当前 ContextGeneration 没有非终结 Turn、没有未决副作用或未完成 operation 时才是 `idle`；`queued`、`running` 或 `outcome_pending` 会使当前 activity 为 `active`。Input acceptance、Runtime side effect、binding 或最终结果不能确认时为 `unknown`，不能当作安全空闲，也不能用新输入自动重放。

- **有进行中的 Turn**：当前 Turn 可能正在排队，也可能由 Runtime 执行。Follow-up 按顺序创建
  SessionInput：后端支持时加入当前 Turn，否则等待后续 Turn。等待队列达到边界时新输入不会
  被接受，已接受的输入不会被丢弃。排队时可以取消，Runtime 开始后可以请求停止当前 Turn。
- **空闲**：没有正在处理的 Turn；Follow-up 创建 SessionInput 和新的 Turn，可以 Compact 或 Reset。
- **未知**：Mohist 暂时无法确认 Runtime 是否已经停止，或无法确认一次输入是否已被
  接受；核对完成前不会把 Session 当作安全空闲，也不会自动重复投递输入。

一次 Turn 完成、失败或停止后，AgentSession 在没有后续 queued Turn 时回到空闲。执行结果保留在
对应的 TaskRun、AgentJob 或 AgentTurn 中，不会把 AgentSession 标记为完成、失败或关闭。Session 不需要
`closed` 生命周期。

AgentSession 的会话内容按发生顺序连续展示。SessionInput 为每条输入提供稳定关联，
AgentTurn 为实际处理过程提供状态；两者都只能通过所属 Session 查找和操作，不建立顶层
列表或独立管理入口。

## 会话事实、操作与上下文边界

Server 是 AgentJob、AgentSession、SessionInput 和 AgentTurn 的 canonical read model。Web、CLI 和 Agent 接入只适配这份事实，不能从本地日志、HTTP 状态、Runner 事件或 provider 响应自行推导状态。每份读取结果都带有 Server 的 `revision` 和 `observedAt`。

AgentJob 的读取结果至少公开 `jobId`、`launchRequestId`、`launchOperationId`、`status`、`outcome`、`reason`、`nextAction` 和当前的 Session/Input/Turn mapping。尚未建立的 mapping 必须是 `null` 并带非空 reason；reservation ID 不能冒充可访问资源。Session、Input 和 Turn 的读取结果分别公开 activity、acceptance、execution status、dispatch status、reason、nextAction 和所属的 `ContextGeneration`，客户端不能把这些事实合并成一个状态。

### 启动身份与响应丢失

启动调用由调用方提供 `launchRequestId`。AgentJob 事务第一次 prepare 时按该 key 幂等创建唯一的 `launchOperationId`、Job、内部 reservation 和 accept-session durable command；reservation 不是可访问资源。Session 事务只原子提交自己的 Session、Input、Turn、request map、dispatch record 和 durable accept/reject event/outbox，不能同时更新 AgentJob。Launch coordinator 以 `launchOperationId` 为唯一身份消费 Session 结果，再在单独的 AgentJob 事务 materialize 三项 live mapping 或 durable rejection。

响应丢失时，客户端先用 `launchRequestId` 找到原 operation，再查询或重试原 `launchOperationId`；Server 必须返回同一组 ID，不得创建第二个 launch。coordinator 重启会扫描 pending command，claim/takeover 后重复消费同一 command；Session accept 成功但 Job 回写失败时，Job 暂时保持 mapping `null + mapping_pending`，直到同一 operation 回写成功。明确拒绝或不可恢复失败是 durable terminal outcome，返回稳定 reason 和 nextAction；临时不可用或 side effect 无法确认时保持 `unknown`，要求查询或人工核对原 operation。

### 操作查询

Compact、Reset、recovery、force-reset、handoff、rebind 和 stop 都必须使用调用方提供的 `operationId`。这个 ID 同时是 query 和 response-loss retry 的身份；查询可以读取当前或历史 operation，并返回唯一 canonical `SessionOperationRead` 的完整字段，包括显式可空 `reason`、同一 phase、outcome、binding、context mapping 和 nextAction。Operation query 不得再次产生副作用、递增 ContextGeneration、创建 candidate 或生成新的 operation。没有 operationId 时 Server 拒绝调用，不生成客户端不可见的替代 key。

Operation projection 是 canonical read model 的一部分。它必须返回该 operation 类型规定的完整字段和显式 null 值；Job、Session、Input 和 Turn 可以引用同一个 `operationId` 或嵌入同一 projection，但不能发明只含状态或只含 mapping 的裁剪 schema。

### Compact、Reset 与 force-reset

- **Compact** 在安全空闲边界执行，保持 AgentSession、当前 Runtime Session 和 ContextGeneration；成功后持久化 ContextBoundary 与 operation result，后续输入继续沿用该 generation。
- **Reset** 在安全空闲边界建立没有旧 Runtime 上下文的新物理 Session，保持 AgentSession、transcript、Input 和 Turn 身份；它递增 ContextGeneration，并以同一 operationId 查询或重试。
- **Force-reset** 只在旧输入、Turn、dispatch attempt、Runtime side effect 或 operation 的结果仍未知且普通操作被阻止时受理。它使用新的 operationId，要求当前 revision、expected ContextGeneration 和显式确认旧 Runtime 可能仍有副作用。唯一 collector 在同一 Session 事务收集这些 target；ActiveOperation（如有）也进入同一个 `supersededTargets` 数组。每个 target 都是 canonical `UnresolvedTargetRead`：`targetKind`、稳定 `targetId`、`requestId`、`contextGeneration`、`originalOperationId`（无已知来源时显式 null）、完整或 null 的 `expectedBinding`、`nextAction` 和 `supersededByOperationId`。没有 ActiveOperation 时仍必须为 unknown facts 建立 target。

  collector、`supersededTargets`、`unresolvedPrevious`、旧 operation 的 supersede 标记、新 operation 的完整 fence 和 `admission=blocked` 先在同一原子事务提交；在新的 candidate binding 与 ContextBoundary 原子提交前不接受新的 Input/Turn。完成提交按同一事务递增 ContextGeneration、写 boundary、将 admission 置 ready，再允许新输入。旧 target 和旧 operation 仍可按 targetId/operationId 查询；响应丢失时重用同一 force-reset operationId，不创建第二个 context。

### ContextGeneration 与未决历史

`ContextGeneration` 从 1 开始，标识当前逻辑上下文，不等同于 operation fence 的 claim generation。普通 Compact 不递增它；Reset、runtime change、missing recovery、force-reset、handoff 和 rebind 开始新的 logical context，并在同一 Session 边界中递增它。每个 Input/Turn 保留创建时的 generation，不能移到新上下文。

当前 activity 只计算当前 generation。较旧 generation 的未决 Input、Turn、side effect 和 operation result 不与当前的 `queued`、`running` 或 `outcome_pending` 混合，而通过 `unresolvedPrevious` 暴露；其中至少包含旧 operationId、旧 ContextGeneration、outcome 和 nextAction，并可提供 unresolved count。只有 force-reset 已确认并 supersede 旧 operation、且新 context/binding 边界已提交后，旧 Unknown 才不再阻止当前 generation 的新 Input/Turn。

### Handoff、rebind 与有界派发失败

- **handoff** 是唯一可以改变 Runner 的显式 operation。Runner 重连、超时或旧事件不能隐式 handoff。
- **rebind** 只能在同一 Runner 上替换 Runtime binding 或物理 Runtime Session，不能借未知事实跨 Runner 迁移。
- 两者都需要 operationId、当前 revision、expected binding 和 bounded deadline；只有当前 generation 为 `idle` 且没有未决 side effect 时受理。当前为 `active`、`outcome_pending` 或 `unknown` 时先查询原 operation，仍未知则选择 force-reset。成功后递增 ContextGeneration，旧 binding 的事件不能改变当前会话。

Input accepted 后的 dispatch retry 必须有 canonical `dispatchAttemptCount`、固定
`dispatchDeadline`、`dispatchAttemptId` 和 `dispatchRetryId`。`dispatchRetryId` 是 outbox
command、timer、due signal 和 coordinator claim 的同一 durable identity；`dispatchRetryDueAt`、
`dispatchRetryOwnerId`、`dispatchRetryClaimGeneration` 和 `dispatchRetryLeaseUntil` 也必须随状态变化持久化。Server 重启后 coordinator 扫描 due work，claim
或 takeover 过期 lease；重复 signal 只消费一次。

达到次数或 deadline 上限时，Input 仍为 accepted，Input ID 和 Turn ID 不变；Server 原子写入
唯一的终态 `dispatchStatus=terminal`、Turn `status=terminal`、Turn `outcome=blocked`、
稳定 `blockedReason` 和可执行 `nextAction`，并取消 retry work，之后不再 retry。临时
`dispatchStatus=blocked` 是可重试非终态：它必须已有 durable due signal，不能被客户端或
coordinator 当作终态，也不能只靠 `nextAction` 唤醒。未知事实也不能被假装成 blocked 或
terminal。

**Stop** 使用唯一 `SessionOperationRead.kind=stop`。`mo session cancel` 仍取消 queued 或
active Turn；调用方必须提供 stable operationId、expected revision/binding 和 bounded deadline。
BeginStop 的 operation row 持久保存 target Turn、完整 FenceToken、owner/claim generation、
lease、reason 和 nextAction。Runtime.stop 前后都 recheck fence；response loss 查询或以同一
operationId 做 bounded retry，结果未知时 Turn 和 operation 保持 `unknown`，不能改成 idle 或
cancelled。

## AgentSession 来源

每个 AgentSession 只有一个来源：

- **Workflow 来源**：由 `WorkflowRun + session 名称` 寻址；同名 task 可以继续上下文。
- **Agent launch 来源**：每次启动 Mohist Agent 时创建，并关联该 Agent ID。
- **Agent 接入来源**：由 Slack 等 Agent Connection 启动，并关联该 Agent ID；它仍是同一个
  Mohist Agent 的会话，不是接入方自己的会话副本。

来源在 Session 整个生命周期内不改变。模型、prompt、执行后端配置相同，不会让两段
Session 合并；当前 Runtime Session 更换也不会改变 AgentSession 来源。

无论来源，CLI 通过顶层 `mo session` 寻址：

- `mo session view <session-id>` / `mo session transcript <session-id>` 都按稳定 Session ID
  读取，不再按来源分两套命令。
- `mo session followup` / `compact` / `reset` / `cancel` 同样只接 Session ID；`cancel` 另需
  `--turn-id`、`--operation-id`、当前 revision/binding 和 bounded deadline。
- `mo session list` 通过 `--agent <agent>` / `--issue <number>` / `--run <run-id>` 之一筛选，来源只是发现条件。
  `--agent` 会列出该 Agent 通过直接启动、Agent Connection 或其他受支持入口创建的会话。
- `mo session cancel` 取消当前 queued 或 active AgentTurn，使用 canonical stop operation。
  queued Turn 在 Session 事务内终结为 cancelled 并清除 durable dispatch retry；active Turn
  先执行 fenced Runtime.stop。若它是 launch 的首个 Turn，AgentJob 以失败类别 `cancelled`
  结束；后续 Turn 被取消不修改原 AgentJob。停止结果未知时保持 operation/Turn `unknown`
  并提供 query/manual-check nextAction。

## 当前 Runtime Session 与缺失恢复

AgentSession ID 是 Mohist 的稳定身份；OpenCode Session 或 Pi Session 是执行后端的
当前物理会话。AgentSession 只保存当前关联，不建立物理 Session 历史。

通常所有后续输入都复用当前 Runtime Session：task 变化、retry、模型变化、Compact、
执行结束或 Runner 重启都不能替换它。只有三种情况建立新的物理 Session——用户
Reset、执行后端明确确认原 Session 已不存在（自动恢复）、明确切换执行后端。替换
不改变 AgentSession ID、来源、工作目录或已记录的会话内容；新 Session 从空上下文
开始，会话中以「上下文已重置」标注，旧消息不重放。

复用不变量、自动恢复边界与并发规则见
[Action 契约 · Agent 执行类 Action 的共享语义](actions/README.md#agent-执行类-action-的共享语义)。

## AgentSession 操作

Workflow 来源和 Agent launch 来源的 AgentSession 使用同一组会话操作：

- **Follow-up**：向当前会话追加用户输入；执行中加入当前执行，空闲时开始新的执行，
  不创建 Mohist Agent 或 AgentJob。
- **Compact**：要求当前执行后端压缩上下文，保持 AgentSession 和当前 Runtime Session。
- **Reset**：在空闲时建立没有旧 Runtime 上下文的新物理 Session，保持 AgentSession
  身份和已有会话内容。

这些操作改变会话，不改变工作所有权。Follow-up 不会把 TaskRun 变成 AgentJob；
Compact 或 Reset 也不会重新启动 Mohist Agent。

## 当前范围

`mohist/opencode` 与 `mohist/pi` 的 Workflow Action 均已实装，具体配置见各自
Action 文档；Mohist Agent 按配置选择 OpenCode 或 Pi，后端随 snapshot 固定到
AgentJob。Web UI 和 CLI 已能创建、编辑、启动 Mohist Agent，并读取和继续 AgentSession。
`mohist/agent` 已定义契约，尚未实装。Mohist Agent 事件响应见
[Agent 事件路由](event-routing.md)。

## 实装差距

Runtime Session 缺失时的自动重建与重新绑定尚未完整落地；当前部分执行路径仍会失败并
要求用户 Reset。对应实施 issue 待从本 spec 创建。

Max concurrent runs 尚未真正限制并发。

Agent Connection 的 Readiness 已提供最小配置推导：AgentConfig 缺少 Model 或 Runtime
时显示 Needs setup，同时保持 Connection health 独立；两者齐备时显示 Ready，尚未探测
的 Agent 默认显示 Unknown。完整的 Runner/runtime 可执行性探测仍是后续工作，当前仍可能
在真正启动后发现更多缺口。

SessionInput 与 AgentTurn 尚未作为稳定的 Session 子记录完整落地；现有 launch/follow-up
返回值、transcript 与 live update 还不能分别回答“哪条输入已受理”和“哪轮 Runtime 正在
处理”。

Agent 接入、Slack Bot、接入权限与连接状态尚未实装。当前调用接口也缺少供外部客户端
安全使用所需的身份验证、重复请求保护和可断线续读的执行事件。目标契约见
[`design/agent-api.md`](../design/agent-api.md) 与
[`design/slack.md`](../design/slack.md)。
