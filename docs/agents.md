# Agent 与 AgentSession

Mohist 使用 Agent 能力有两种方式：Workflow 可以直接内联调用，Project 也可以保存
有稳定身份的 Mohist Agent。两者都会使用 AgentSession，但 Action、Agent、Job 和
Session 分属不同层次。

## 概念层次

| 概念 | 是什么 | 身份和生命周期 |
|---|---|---|
| Inline Agent | Workflow 直接配置并调用 Agent 能力的用法 | 不是资源，没有 Agent ID；配置随 task 输入存在 |
| Agent 定义引用 | Workflow task 用 `uses: mohist/agent` 引用 Mohist Agent 定义的用法 | 不是资源，没有 Agent ID；定义快照随 dispatch 解析 |
| Mohist Agent | Project 内预先定义、按名称复用的 Agent 资源 | 有稳定 Agent ID、名称、指令、配置、Skills 和状态 |
| AgentJob | Mohist Agent 的一次工作 | 独立记录等待、执行、完成或失败，以及本次结果 |
| AgentSession | Mohist 记录的一段持续会话 | 有稳定 Session ID；保存消息、上下文、用量、活动状态和当前 Runtime Session |
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

Mohist Agent 也称 Named Agent，是 Project 内的一等资源。它保存：

- 稳定 ID 和名称；
- Instructions 和 Agent 配置；
- Skills；
- 并发限制与 active / archived 状态。

Mohist Agent 有三种启动方式：用户手动启动；项目的事件路由规则命中后自动启动；
在 issue 的 comment 里 `@` 它的名字点名启动。提及把评论正文作为本次输入，并
自动带上该 issue 的上下文——这是一次性工作，适合「@my-agent 监督并推进这个
issue」这样的当面包办；如果要求的是持续关注，Agent 会自己用 `mo issue watch add` 把这个 issue 加入关注。
无论哪种方式，启动时都会创建
AgentJob，并固定本次使用的 Agent 指令和配置；之后编辑 Agent，不改变已经开始的工作。

Mohist Agent 的核心位置是代理人：它进入流水线上原本由 owner 负责的位置，通过
和人相同的命令与审批通道执行动作。一个 Mohist Agent 可以有多个 AgentJob，也可以
有多个 AgentSession。

## AgentJob 与 AgentSession

AgentJob 和 AgentSession 经常同时创建，但职责不同：

| | AgentJob | AgentSession |
|---|---|---|
| 回答的问题 | 这次 Mohist Agent 工作完成了吗 | 这段会话发生了什么、现在能否继续输入 |
| 拥有 | 调度状态、成功或失败、执行结果 | 消息、上下文、用量、活动状态和当前 Runtime Session |
| 生命周期 | 一次工作，最终完成或失败 | 持续存在，可以接受多次输入 |
| 所属概念 | Mohist Agent 的工作 | Session 记录 |

Workflow 的对应工作所有者是 TaskRun，而不是 AgentJob。TaskRun 或 AgentJob 负责裁定
工作结果；AgentSession 只记录执行事实，不推进 Workflow，也不裁定 AgentJob。

## Session 活动状态

AgentSession 的结构和用户心智模型靠近 OpenCode、Pi 等会话：它持续保存消息，同时
呈现当前是否正在处理输入。

- **执行中**：Runtime 正在处理当前输入；Follow-up 进入当前执行，可以取消当前执行。
- **空闲**：没有正在处理的输入；Follow-up 开始一次新的执行，可以 Compact 或 Reset。
- **未知**：Mohist 暂时无法确认 Runtime 是否已经停止，或无法确认一次输入是否已被
  接受；核对完成前不会把 Session 当作安全空闲，也不会自动重复投递输入。

一次执行完成、失败或停止后，AgentSession 回到空闲。执行结果保留在对应的 TaskRun、
AgentJob 或会话内容中，不会把 AgentSession 标记为完成、失败或关闭。Session 不需要
`closed` 生命周期。

AgentSession 的会话内容按发生顺序连续展示。每次输入是这段会话中的普通输入边界，
不是另一个有身份、有生命周期的资源。

## AgentSession 来源

每个 AgentSession 只有一个来源：

- **Workflow 来源**：由 `WorkflowRun + session 名称` 寻址；同名 task 可以继续上下文。
- **Agent launch 来源**：每次启动 Mohist Agent 时创建，并关联该 Agent ID。

来源在 Session 整个生命周期内不改变。模型、prompt、执行后端配置相同，不会让两段
Session 合并；当前 Runtime Session 更换也不会改变 AgentSession 来源。

## 当前 Runtime Session

AgentSession ID 是 Mohist 的稳定身份。OpenCode Session 或 Pi Session 是执行后端的
当前物理会话。AgentSession 只保存当前关联，不另外建立物理 Session 历史。

通常所有后续输入都复用当前 Runtime Session。task 变化、retry、模型变化、Compact、
一次执行结束或 Runner 重启都不能替换它。以下情况可以建立新的物理 Session，并让
AgentSession 改用新关联：

- 用户在 Session 空闲时明确执行 Reset；
- 当前执行后端明确确认原物理 Session 已不存在；
- 同一 AgentSession 明确切换到另一执行后端。

替换不会改变 AgentSession ID、来源、工作目录或已记录的会话内容，也不会把旧消息重新
提交给新 Runtime Session。新物理 Session 从空上下文开始；会话中只需记录 Reset、恢复
或后端切换造成的“上下文已重置”，不展示或维护一份物理 Session 沿革。

旧物理 Session 是否仍保留、是否占用缓存或何时清理由执行后端决定，不属于
AgentSession 生命周期。

## Runtime Session 缺失恢复

Runtime Session 可能被执行后端回收，也可能在 Runner 重启后无法恢复。这不应让仍可
继续的 AgentSession 永久卡在失效关联上。

Mohist 在提交一条新的独立输入前，通过负责当前关联的 Runner 检查 Runtime Session。
执行后端明确确认它已经不存在时，Mohist 在同一 Runner 建立一个空 Runtime Session，
替换当前关联，然后才接受本次输入。Workflow task、AgentJob 和 Session 空闲时发起的
Follow-up 使用同一规则，用户不需要先 Reset 再重试。

自动恢复遵守以下边界：

- AgentSession ID、来源、工作目录和已记录内容保持不变。
- 新 Runtime Session 从空上下文开始；Mohist 不重放旧 Prompt、消息或工具调用。
- 只有“原 Runtime Session 明确不存在”可以触发自动恢复。执行后端暂时不可用、请求
  超时、权限失败、响应无法判断或数据不兼容时，本次操作明确失败，当前关联保持不变。
- 请求落到其它 Runner 时，不用该 Runner 的本地结果推断原 Session 已不存在，也不借
  自动恢复迁移 Runner。
- 输入可能已经被执行后端接受，或 AgentSession 为执行中或未知时，不替换关联，也不
  重新投递。
- 并发操作不能覆盖已经变化的当前关联。未能确认替换成功时，本次输入不会提交。

Compact 和 Cancel 必须作用于当前 Runtime Session，不触发自动重建。Reset 表示用户
主动丢弃 Runtime 上下文；原 Runtime Session 已不存在也不阻止 Reset 创建空 Session。

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

- `mohist/opencode`：直接通过 OpenCode 执行一次输入；Workflow 直接使用时形成 Inline Agent。
- `mohist/pi`：直接通过 Pi 执行一次输入，和 `mohist/opencode` 处于同一层。
- `mohist/agent`：引用预定义 Mohist Agent 的定义执行 task（定义引用，不创建 AgentJob），契约见 [`mohist/agent` Action](actions/agent.md)。

`mohist/opencode` 与 `mohist/pi` 的 Workflow Action 均已实装；Mohist Agent 也可以按配置
选择 OpenCode 或 Pi。`mohist/agent` 已定义契约，尚未实装。

Mohist Agent 的配置中包含执行后端选择（OpenCode 或 Pi）；启动时后端随 Agent snapshot
固定到 AgentJob，执行中编辑 Agent 不改变已经开始的工作。

各 Action 的具体配置见 [`mohist/opencode`](actions/opencode.md) 与
[`mohist/pi`](actions/pi.md)。Mohist Agent 事件响应见 [Agent 事件路由](event-routing.md)。

## 实装差距

当前部分路径仍把一次执行的结果解释为整个 AgentSession 的终态，并产生旧的结束事件；
#484 负责按本 spec 收敛为可复用的活动状态。当前实现还保存和展示物理 Session 沿革，
目标模型不再包含该结构。

Runtime Session 缺失时的自动重建与重新绑定尚未完整落地；当前部分执行路径仍会失败并
要求用户 Reset。对应实施 issue 待从本 spec 创建。
