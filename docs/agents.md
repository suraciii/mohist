# Agent 与 AgentSession

Mohist 使用 Agent 能力有两种方式：Workflow 可以直接内联调用，Project 也可以保存
有稳定身份的 Mohist Agent。两者都会产生 AgentSession，但 Action、Agent、Job 和
Session 分属不同层次。

## 概念层次

| 概念 | 是什么 | 身份和生命周期 |
|---|---|---|
| Inline Agent | Workflow 直接配置并调用 Agent 能力的用法 | 不是资源，没有 Agent ID；配置随 task 输入存在 |
| Mohist Agent | Project 内预先定义、按名称复用的 Agent 资源 | 有稳定 Agent ID、名称、指令、配置、Skills 和状态 |
| AgentJob | Mohist Agent 的一次执行 | 独立记录等待、执行、完成或失败，以及本次结果 |
| AgentSession | 一段持续对话的逻辑记录 | 有稳定 Session ID；保存消息、上下文、用量和会话沿革 |
| Turn | AgentSession 内的一次对话执行 | 接受一个或多个输入，最终完成、失败或停止 |
| Runtime Session | OpenCode、Pi 等执行后端实际维护的对话 | 由执行后端标识；Reset、更换后端或确认原 Session 已不存在时可以更换 |

Action 不在 Agent 资源层：`mohist/opencode` 描述一次工作如何交给 OpenCode，
不代表一个有身份的 Agent。

## 两条调用路径

| 使用路径 | 是否有 Agent 身份 | 谁负责本次工作 | 如何执行 | AgentSession 来源 |
|---|---|---|---|---|
| Workflow 直接调用 | 否；这是 Inline Agent | TaskRun | 执行后端 Action（`mohist/opencode`、`mohist/pi`） | Workflow |
| 启动 Mohist Agent | 是；使用已保存的 Mohist Agent | AgentJob | Mohist Agent 的内部执行入口 | Agent launch |

两条路径可以使用同一种执行后端能力和同一种 AgentSession 模型，但不会共享 Agent
身份或工作生命周期。Workflow 通过执行后端 Action 调用 OpenCode 或 Pi；Mohist Agent
由 AgentJob 执行，只在底层复用执行后端能力，并不反过来调用 Workflow Action。

## Inline Agent

Inline Agent 是一种使用方式，不是持久化实体。Workflow task 直接声明：

- 用哪个执行后端 Action，例如 `mohist/opencode`；
- 这次执行的 prompt；
- 可选的 Session 名称和 OpenCode 模型选项。

它适合 Workflow 中的规划、实现、审查和修复。它没有名称、Instructions、Skills
或 Agent ID，不能被事件路由规则引用，也不能被 `mo agent` 命令查找。

Workflow TaskRun 拥有这次 task 的成功、失败和输出。Action 只是执行接口，
AgentSession 只是对话与审计记录。

## Mohist Agent

Mohist Agent 也称 Named Agent，是 Project 内的一等资源。它保存：

- 稳定 ID 和名称；
- Instructions 和 Agent 配置；
- Skills；
- 并发限制与 active / archived 状态。

用户可以手动启动 Mohist Agent，项目的事件路由规则命中后也可以启动它
（规则引用 Agent，Agent 不拥有规则）。启动时会创建
AgentJob，并固定本次使用的 Agent 指令和配置；之后编辑 Agent，不改变已经开始的
执行。

Mohist Agent 的核心位置是代理人：它进入流水线上原本由 owner 负责的位置，通过
和人相同的命令与审批通道执行动作。一个 Mohist Agent 可以有多个 AgentJob，也可以
有多个 AgentSession。

## AgentJob 与 AgentSession

AgentJob 和 AgentSession 经常同时创建，但职责不同：

| | AgentJob | AgentSession |
|---|---|---|
| 回答的问题 | 这次 Mohist Agent 执行完成了吗 | 这段对话发生了什么 |
| 拥有 | 调度状态、成功/失败、执行结果 | 消息、上下文、用量、当前执行会话和会话沿革 |
| 生命周期 | 一次执行，最终完成或失败 | 可跨多个回合持续存在 |
| 所属概念 | Mohist Agent 的工作 | Session 记录 |

Workflow 的对应工作所有者是 TaskRun，而不是 AgentJob。TaskRun 或 AgentJob 负责
裁定工作结果；AgentSession 只记录执行事实，不推进 Workflow，也不裁定 AgentJob。

## Turn 与 Session 状态

AgentSession 可以包含多个 Turn。一个 Turn 完成、失败或停止，只说明这一次对话执行已经
结束；AgentSession 随后回到空闲，可以继续 Follow-up。它不会因为最近一个 Turn 的结果
被标记为完成、失败或关闭。

Session 的活动状态与最近 Turn 的结果分别呈现：

- **执行中**：当前 Turn 正在执行；Follow-up 进入当前 Turn，可以取消当前 Turn。
- **空闲**：没有执行中的 Turn；Follow-up 开始新 Turn，可以 Compact 或 Reset。
- **未知**：Mohist 暂时无法确认当前 Turn 是否已经停止；在核对完成前不会把 Session
  当作安全空闲，也不会自动重复投递 Follow-up。

执行后端持有的 Runtime Session 可以在内存中缓存，也可以被恢复或回收。这是执行资源
管理，不等同于 AgentSession 关闭，也不改变已记录的对话。

## AgentSession 来源

每个 AgentSession 只有一个来源：

- **Workflow 来源**：由 `WorkflowRun + session 名称` 寻址；同名 task 可以继续上下文。
- **Agent launch 来源**：每次启动 Mohist Agent 时创建，并关联该 Agent ID。

来源在 Session 整个生命周期内不改变。模型、prompt、执行后端配置相同，不会让两段
Session 合并；Runtime Session 更换也不会改变 AgentSession 来源。

AgentSession ID 是 Mohist 的逻辑身份。OpenCode Session 等 Runtime Session 是执行
后端的身份。Compact 保持底层 Session 不变；Reset、后端变化或缺失恢复可以建立新的
Runtime Session，同时在同一个 AgentSession 中保留会话沿革。

## Runtime Session 缺失恢复

Runtime Session 可能被执行后端回收，也可能在 Runner 重启后无法恢复。这不应让仍可
继续的 AgentSession 永久卡在旧绑定上。

Mohist 在开始一个新 Turn 前，通过负责当前绑定的 Runner 检查 Runtime Session。执行
后端明确确认它已经不存在时，Mohist 在同一 Runner 自动建立一个空的 Runtime Session，
重新绑定原 AgentSession，然后才接受本次输入。Workflow task、AgentJob 和 Session 空闲
时发起的 Follow-up 使用同一规则，用户不需要先 Reset 再重试。

自动恢复遵守以下边界：

- AgentSession ID、来源和工作目录保持不变；会话沿革记录这次替换及其原因。
- 新 Runtime Session 从空上下文开始。Mohist 不重放旧 Prompt、消息或工具调用，也不把
  新上下文伪装成原物理对话仍然存在。
- 只有“原 Runtime Session 明确不存在”可以触发自动恢复。执行后端暂时不可用、请求
  超时、权限失败、响应无法判断或数据不兼容时，当前操作明确失败，绑定保持不变。
- 请求落到其它 Runner 时，不用该 Runner 的本地结果推断原 Session 已不存在，也不借
  自动恢复迁移绑定。
- 输入可能已经被执行后端接受，或 AgentSession 的活动状态为执行中或未知时，不自动
  替换或重新投递。
- 并发操作不能覆盖已经变化的绑定。未能确认重新绑定时，本次输入不会提交。

Compact 和 Cancel 需要作用于原 Runtime Session，不触发自动重建。Reset 表示用户主动
丢弃上下文；无论原 Runtime Session 是否仍存在，它都会在 Session 空闲时建立新的空
Session。

缺失恢复是固定的 AgentSession 语义，不新增 Workflow、Action 或 Agent 配置开关。用户
不能要求 Mohist 在提交状态不确定时强制重建或重放。

## AgentSession 操作

Workflow 来源和 Agent launch 来源的 AgentSession 使用同一组会话操作：

- **Follow-up**：向当前逻辑会话追加用户输入；执行中进入当前回合，空闲时开始一个
  用户发起的会话回合，不创建新的 Mohist Agent 或 AgentJob。
- **Compact**：要求当前执行后端压缩上下文，保持 AgentSession 身份。
- **Reset**：主动建立没有旧上下文的新 Runtime Session，保留原 AgentSession 和会话
  沿革；原 Runtime Session 已不存在时也可以执行。

这些操作改变会话，不改变工作所有权。Follow-up 不会把 TaskRun 变成 AgentJob；
Compact 或 Reset 也不会重新启动 Mohist Agent。具体执行方式由当前执行后端决定。

## 当前范围

- `mohist/opencode`：直接通过 OpenCode 执行一个回合；Workflow 直接使用时形成 Inline Agent。
- `mohist/pi`：直接通过 Pi 执行一个回合，和 `mohist/opencode` 处于同一层。

`mohist/opencode` 与 `mohist/pi` 的 Workflow Action 均已实装。Mohist Agent 当前固定使用
OpenCode，按 Agent 配置选择 Pi 的 AgentJob 路径仍由对应 issue 推进。`mohist/agent` 不在
本次范围内；它保留给后续 Mohist Agent 专项设计，本篇不定义它的输入、复用或等待语义。

Mohist Agent 的配置中包含执行后端选择（OpenCode 或 Pi）；启动时后端随 Agent snapshot
固定到 AgentJob，执行中编辑 Agent 不改变已经开始的执行。

各 Action 的具体配置见 [`mohist/opencode`](actions/opencode.md) 与
[`mohist/pi`](actions/pi.md)。Mohist Agent 事件响应见 [Agent 事件路由](event-routing.md)。

## 实装差距

Runtime Session 缺失时的自动重建与重新绑定尚未完整落地；当前部分执行路径仍会失败并
要求用户 Reset。对应实施 issue 待从本 spec 创建。
