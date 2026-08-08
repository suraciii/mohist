# Agent 与 AgentSession

Mohist Agent 是 Project 内可独立配置和使用的 Agent。用户可以在 Web UI 或 CLI 中直接启动它，
也可以把同一个 Agent 接入 Slack，或让它响应事件与评论提及。入口可以变化，Agent 的身份、
Instructions、执行配置、Skills、AgentJob 和 AgentSession 不变。

第三方的外部 Agent 是另一条路径：它通过 Mohist Skill 和 `mo` 查询、委托或操作执行层，
不是 Mohist 资源。只有当它显式启动一个 Mohist Agent 时，才会产生该 Mohist Agent 的
AgentJob 与 AgentSession。完整产品边界见[核心概念](concepts.md)。

## 产品承诺

- **Agent 先独立可用**：没有 Slack 等外部接入时，用户也能完整配置、启动、继续对话、读取
  结果和处理异常。
- **配置只有一份**：Instructions、执行后端、模型、Variant、Skills 和并发限制由 Mohist Agent
  拥有；Web、CLI 和 Agent 接入不能保存或覆盖另一份定义。
- **入口不改变语义**：一次新的委托创建 AgentJob、AgentSession、首条 SessionInput 和首个
  AgentTurn；对已有会话继续输入会创建新的 SessionInput，但不创建第二个 AgentJob。
- **执行状态可追溯**：AgentJob 回答首次启动是成功、被拒绝还是不可恢复地失败，AgentSession
  回答发生了什么、每次后续输入的结果以及当前能否继续。Slack 消息或 Web 页面都不是状态裁判。
- **已经接受的输入不会消失**：输入一旦被 Mohist 接受，就不会因断线、Runner 重启、背压或
  新消息而被丢弃、换 ID 或静默改写。

## 概念层次

| 概念 | 是什么 | 身份和生命周期 |
|---|---|---|
| Inline Agent | Workflow 直接配置并调用 Agent 能力的用法 | 不是资源，没有 Agent ID；配置随 task 输入存在 |
| Agent 定义引用 | Workflow task 用 `uses: mohist/agent` 引用 Mohist Agent 定义的用法 | 不是资源，没有 Agent ID；定义在 task 开始执行时固定 |
| Mohist Agent | Project 内预先定义、按名称复用的 Agent 资源 | 有稳定 Agent ID、名称、指令、配置、Skills 和状态 |
| Agent 接入 | 把一个 Mohist Agent 暴露到 Slack 等外部交互场所 | 有独立连接生命周期；只引用 Agent，不拥有或复制 Agent 配置 |
| AgentJob | Mohist Agent 的一次 launch 执行 | 独立记录等待、执行、完成、拒绝或失败，以及首次执行结果 |
| SessionInput | AgentSession 接受的一条输入 | 有稳定 Input ID；记录内容、附件、来源、顺序和投递状态 |
| AgentTurn | Runtime 连续处理一组有序 SessionInput 的过程 | 有稳定 Turn ID 和状态；由 AgentSession 拥有，不是新的顶层工作 |
| AgentSession | Mohist 记录的一段持续会话 | 有稳定 Session ID；按顺序拥有 Input 与 Turn，并保存上下文、用量、活动状态和当前 Runtime Session |
| Runtime Session | OpenCode、Pi 等执行后端实际维护的物理会话 | 由执行后端标识；必要时可以被 AgentSession 替换 |

Action 不在 Agent 资源层：`mohist/opencode` 描述一次工作如何交给 OpenCode，不代表一个有
身份的 Agent。

## 两条调用路径

| 使用路径 | 是否有 Agent 身份 | 谁负责本次工作 | 如何执行 | AgentSession 来源 |
|---|---|---|---|---|
| Workflow 直接调用 | 否（Inline Agent 或 Agent 定义引用） | TaskRun | 执行后端 Action 或 `mohist/agent` | Workflow |
| 启动 Mohist Agent | 是；使用已保存的 Mohist Agent | AgentJob | Mohist Agent 的内部执行入口 | Agent launch |

两条路径可以使用同一种执行后端能力和同一种 AgentSession 模型，但不会共享 Agent 身份或
工作生命周期。Workflow 通过执行后端 Action 调用 OpenCode 或 Pi；Mohist Agent 由 AgentJob
执行，只在底层复用执行后端能力。

## Inline Agent

Inline Agent 是一种使用方式，不是持久化实体。Workflow task 直接声明执行后端 Action、
这次执行的 prompt，以及可选的 Session 名称和模型选项。它适合 Workflow 中的规划、实现、
审查和修复，没有名称、Instructions、Skills 或 Agent ID，不能被事件路由规则引用，也不能
被 `mo agent` 命令查找。Workflow TaskRun 拥有这次 task 的成功、失败和输出；AgentSession
只保存会话内容和执行事实。

## Agent 定义引用

task 也可以改用 `uses: mohist/agent` 并给出 `name`，引用一个预定义 Mohist Agent 的指令与
执行配置。这不是 Inline Agent，也不是启动 Mohist Agent，不创建 AgentJob；TaskRun 拥有成败，
AgentSession 仍是 Workflow 来源。契约见 [`mohist/agent` Action](actions/agent.md)。

## Mohist Agent

Mohist Agent 是 Project 内的一等资源。它保存稳定 ID、名称、头像、描述、Instructions、执行
配置、Skills、并发限制与 active / archived 状态。模型供应商与 Runtime 凭据在受保护的
Runtime 设置中配置，不写进 Instructions，也不复制到 Agent 或 Agent 接入。

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

本次委托可以附带 Issue、Epic、Repository 等上下文引用，但上下文不是 Agent 配置。普通客户端
只能提供任务文本和上下文，不能覆盖这些执行定义或并发限制。Agent 的定义在一次 launch 或
Workflow Agent task attempt 开始时固定；该次执行加载的 Skills 也随之固定。

名称、头像和描述是展示身份，编辑后立即用于 Mohist 的发现与展示；Agent 接入异步同步支持
更新的外部身份，并明确显示未同步状态。编辑 Instructions、Runtime、Model、Variant 或 Skills
只影响之后创建的 AgentJob。已有 AgentSession 的后续输入继续使用该会话建立时的配置与上下文。
Max concurrent runs 是 Agent 当前的调度策略，所有 Session 的下一次执行都按最新值排队；
修改它不改变任何 Session 的执行定义。

名称、头像和描述用于 Mohist 的发现与展示。Instructions、Runtime、Model、Variant 和 Skills
只影响之后创建的 AgentJob。每个 AgentJob 保存启动时的执行快照；已有 AgentSession 的后续
输入继续使用该会话建立时的配置与上下文，不因 Agent 被编辑而悄悄换模型或能力。

## Readiness 与可用性

Agent 的 active / archived 只回答“是否接受新委托”。Readiness 回答 Mohist 当前能否确认
Agent 的执行配置完整：

| Readiness | 含义 | 用户动作 |
|---|---|---|
| Ready | Mohist 已确认当前定义可以执行 | 可以测试或启动 |
| Needs setup | Mohist 已确认存在配置缺口 | 阻止启动，并列出缺口和修复入口 |
| Unknown | Mohist 暂时无法确认是否可执行 | 可以提交并等待验证，但不能宣称已经可用 |

Runner 暂时离线或没有空闲容量属于 Availability，不把 Ready Agent 改成 Needs setup；工作
可以被接受并排队。已经排队的 AgentJob 在 Runner 或容量恢复后显示为等待调度，直到下一次
调度尝试开始；这不是新的配置缺口。

### 在 Web UI 中配置和测试

1. 在 **Agents** 中创建或打开 Agent，填写名称、头像、描述和 Instructions。
2. 选择 Runtime 后，只展示该 Runtime 支持的 Model、Variant 与凭据要求；再选择 Skills 和
   并发限制。页面必须显示 Readiness 和每个缺口。
3. Readiness 为 Ready 后，使用 **Start session** 提交任务；Unknown 时也可以提交，但页面
   必须说明任务会等待验证。创建成功后进入 AgentSession。
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

`agent view` 显示 Readiness、Availability 与配置缺口；Needs setup 时按提示补齐再启动。
`agent launch` 返回 AgentJob、AgentSession、首个 Input 和 Turn。首次启动的结果用返回的
observation 读取；连续对话用 `mo session followup`，完整记录用 `mo session transcript`。
pending、queued 和执行中、结果待确认的状态继续观察，terminal 状态读取结果或 transcript，
Unknown 必须用原启动 key 重读或重试。

## 启动与继续

Web、CLI、Agent 接入和事件路由都使用同一套启动语义：一次新的委托创建 AgentJob、
AgentSession、首条 SessionInput 和首个 AgentTurn。首次启动的工作结果由 AgentJob 负责；
连续对话由原 AgentSession 的 follow-up 负责。

启动调用必须带一个由调用方保留的启动 key。响应丢失时，用户用同一个启动 key 重新查看或
重试；Mohist 返回原 AgentJob、AgentSession、Input 和 Turn，不创建第二组记录。启动明确被
拒绝或确认不可恢复失败时，Mohist 返回稳定原因和下一步，不把尚未建立的会话、输入或轮次
伪装成可访问资源。结果尚未确定时保持未知，用户先查看原启动，不用新 key 保险重试。

对已有会话的 follow-up 只创建新的 SessionInput 和必要的 AgentTurn，不创建第二个 AgentJob。
空闲时 follow-up 开始新的轮次；执行中若执行后端支持 steer，可以加入当前轮次，否则按顺序
等待。等待队列有边界，边界满时拒绝新输入；已经接受的输入仍然保留。

输入一旦被接受，即使入队或 Runner 通信失败也不会被删除、换 ID 或改成未接受。Mohist 会在
有限次数和有限时间内继续尝试；永久派发失败会显示为明确的阻塞结果和下一步，而不是无限
保持等待。用户处理该结果时仍查看同一个 Input 和 Turn。

## 启动入口

| 入口 | 新委托是什么 | Mohist 中发生什么 |
|---|---|---|
| Web UI | 选择 Agent，输入任务和可选上下文 | 创建 AgentJob、AgentSession、首条 Input 和首个 Turn，并进入会话页 |
| CLI | `mo agent launch <agent>` | 创建相同的 AgentJob、AgentSession、首条 Input 和首个 Turn，返回对应 ID |
| Agent 接入 | Slack 私聊中的首项任务、明确的 New task，或新的频道根提及 | 接入把消息交给已绑定的 Agent，不改变 Agent 配置 |
| 事件路由 | 规则命中的事件和响应提示词 | 为该事件创建一次 AgentJob 与 AgentSession |
| Issue 评论提及 | `@<agent-name>` 后的评论内容 | 以评论为任务，并关联该 Issue 上下文 |

提及把评论正文作为本次输入，并自动带上该 issue 的上下文；这是一次性工作。启动时固定本次
使用的 Agent 指令和配置，之后编辑 Agent 不改变已经开始的工作。一个 Mohist Agent 可以有
多个 AgentJob 和 AgentSession，也可以在自己的会话里 spawn 子会话形成会话树，见
[Subagent 与会话树](subagents.md)。接入 Slack 的线程与权限规则见 [Slack](slack.md)。

## AgentJob 与 AgentSession

| | AgentJob | SessionInput | AgentTurn | AgentSession |
|---|---|---|---|---|
| 回答的问题 | 这次启动工作成功了吗 | 这条输入是否已接受、排队或交给 Runtime | 这一轮处理到了哪里、结果是什么 | 这段会话发生了什么、现在能否继续输入 |
| 生命周期 | 一次启动工作，最终完成、拒绝或失败 | 一条输入，最终被提交或明确拒绝；投递事实也可能暂时未知 | 一次连续执行，最终完成、失败、取消或暂时未知 | 持续存在，可以接受多次输入 |

AgentJob 的 `completed` 不表示整段对话关闭，也不保证用户口头描述的宽泛目标已经交付。
需要持续追踪到 Done 的业务工作应由 Agent 创建或推进 Issue / Workflow；不要靠一个永不
结束的聊天 Job 代替执行层。

首个 AgentTurn 与 AgentJob 关联；后续 AgentTurn 不修改 AgentJob。Agent 可以在回复中提出问题，
此时 AgentJob 可以已经完成，AgentSession 回到空闲，用户通过 follow-up 继续。每次 follow-up
创建有稳定 ID 的新 SessionInput：空闲时开始新的 AgentTurn；执行中可由当前 Turn steer 接受，
不支持 steer 时按顺序等待下一轮。两种情况都不创建新的 AgentJob，也不重写首次启动的结果。

## Session 活动状态

- **有进行中的轮次**：当前轮次可能正在排队，也可能由 Runtime 执行；Follow-up 按顺序创建
  SessionInput。排队时可以取消，Runtime 开始后可以请求停止当前轮次。
- **结果待确认**：Mohist 已知输入被接受并尝试推进，但最终结果还没有记录；这不是成功、
  失败或空闲。
- **空闲**：没有未完成轮次，也没有需要处理的会话操作；这是唯一可以安全开始新输入、
  Compact 或 Reset 的状态。
- **未知**：Mohist 暂时无法确认输入是否已被接受、执行是否已停止，或外部动作是否已经发生。
  核对完成前不会把会话当作安全空闲，也不会自动重复投递输入。

一次轮次完成、失败或停止后，AgentSession 在没有后续排队轮次或待确认结果时回到空闲。
AgentSession 没有 completed、failed、stopped 或 closed 生命周期。会话内容按发生顺序连续
展示，Input 和 Turn 只能通过所属 Session 查找和操作，不建立顶层列表或独立管理入口。

## 操作、操作 ID 与响应丢失

Follow-up、Compact、Reset、恢复、force-reset、handoff 和 rebind 都作用于同一个
AgentSession，不改变 AgentSession 身份、来源、Workspace 或已有 transcript。

每一项 Compact、Reset、恢复、force-reset、handoff 和 rebind 都必须有一个调用方可保留、可
查询、可重试的 operationId。这个 ID 是用户观察同一项操作的身份：请求超时、断线、Runner
重启或响应丢失后，查询和重试都使用原 operationId，并返回同一阶段、结果、绑定和上下文
边界。Mohist 不因为没有收到响应而生成第二项操作，也不允许客户端用轮询把永久阻塞伪装成
无限 pending。

### Compact 与 Reset

- **Compact** 在安全空闲边界请求执行后端压缩上下文，保持 AgentSession 和当前 Runtime Session；
  成功后保留同一会话继续输入。
- **Reset** 建立没有旧 Runtime 上下文的新物理会话，保持 AgentSession 身份、已有内容和
  Workspace。它只在安全空闲时受理。
- 两者都保留 transcript、Input、Turn 和结果。操作结果未知时，用户先用原 operationId
  查询或重试，不能自动建立第二个上下文。

### Force-reset

Force-reset 是用户明确承担未知副作用风险后的逃生路径，不是普通 Reset，也不会抹掉旧事实。
只有当前会话仍被未知的输入、轮次或操作阻止继续时才允许选择；用户必须确认旧 Runtime 可能
仍在工作、旧动作可能重复产生效果，以及旧结果需要人工核对。

Force-reset 使用新的 operationId。Mohist 先保留旧 Input、Turn、操作和风险为未知，并确认
用户看到的会话版本仍然是当前版本；新上下文和新 Runtime Session 的边界持久成功后，才允许
新的 Input/Turn 进入。旧会话内容、旧 Session ID 和旧未知事实继续可查询，当前页面同时显示
新上下文的活动、旧未决数量和下一步。Force-reset 响应丢失时仍使用同一个新的 operationId，
不会创建第二个上下文。

### Handoff 与 rebind

- **handoff** 是唯一可以把会话交给另一个 Runner 的显式操作。Runner 重启、断线、超时或
  旧事件不能自动触发 handoff。
- **rebind** 只在同一个 Runner 上替换 Runtime 或物理 Runtime Session。它不能借未知事实
  把会话迁移到另一个 Runner，也不能把 Runtime 缺失猜成已确认缺失。
- 两者都使用 operationId，并在用户看到的当前会话版本、当前绑定和有限截止时间下受理。
  当前轮次 active、结果待确认或未知时拒绝；用户先查看原操作，仍未知时选择 force-reset。
- 成功后，AgentSession、Workspace、transcript、Input 和 Turn 身份不变；新的上下文边界
  对用户可见。旧 Runner 或旧 Runtime 的迟到事实不能改变当前会话。

普通 Runtime 重连只复用当前绑定；只有执行后端明确确认物理会话不存在，且没有未决副作用时，
Mohist 才能在同一 Runner 上自动恢复。超时、非 404 错误、权限失败或响应异常都是未知，不
触发换绑或输入重放。

## AgentSession 来源和寻址

每个 AgentSession 只有一个不可变来源：Workflow、Agent launch 或 Agent Connection。来源在
整个生命周期内不改变；模型、prompt 或执行后端相同也不会合并两段会话。

无论来源，用户都按稳定 Session ID 查看、继续、Compact、Reset、取消和读取 transcript。
按 Agent、Issue 或 WorkflowRun 筛选只是发现条件，不是另一套会话身份。

CLI 的 `mo session view <session-id>`、`mo session transcript <session-id>`、`mo session followup`、
`compact`、`reset` 和 `cancel` 都按稳定 Session ID 工作。来源只是筛选条件；同一 Agent 通过
直接启动、Agent Connection 或其他受支持入口创建的会话都出现在统一的 Session 视图中。

## 当前范围与实装差距

`mohist/opencode` 与 `mohist/pi` 的 Workflow Action 已有文档；Mohist Agent 的配置、启动、
查看和继续会话已有基础路径。Runtime Session 缺失时的自动重建、可靠的重复请求保护、
跨断线续读、handoff、rebind、force-reset 和有界调度重试尚未完整落地，部分路径仍会要求
用户 Reset。本文是目标产品契约，实装由对应 issue 追赶；差距不改变上面的生命周期承诺。

Max concurrent runs 尚未真正限制并发。Agent Connection 的 Readiness 已提供最小配置推导；
完整的 Runner/runtime 可执行性探测仍是后续工作。SessionInput 与 AgentTurn 尚未在所有入口
完整落地，当前部分返回值仍不能分别回答“哪条输入已受理”和“哪轮 Runtime 正在处理”。

Agent Connection、Slack Bot、接入权限与连接状态的完整能力仍在后续工作。技术边界和
canonical 状态契约见 [`design/agent-api.md`](../design/agent-api.md) 与
[`design/agent-execution.md`](../design/agent-execution.md)。
