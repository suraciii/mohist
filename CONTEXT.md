# Mohist 统一语言

本词汇表定义 Mohist 各上下文共享的产品与 Agent 执行语言。生命周期、事件与模块边界的
规范见 [`design/agent-execution.md`](design/agent-execution.md)。

## 产品界面

**外部 Agent**：用户在 Mohist 之外直接交互，并代表用户读取或操作 Mohist 的 Agent。
它不是 Mohist 资源，也不由 Mohist 调度或运行。

避免用法：用“Mohist Agent”或“Inline Agent”指代外部 Agent。

**Agent 接入（Agent Connection）**：把一个 Mohist Agent 暴露到某个外部交互场所的
持久关系。它保存该场所中的 Agent 身份、调用范围和可用状态，但不复制 Mohist Agent
的 Instructions、执行配置或 Skills，也不拥有 AgentSession。

**Mohist App**：一个 Slack 工作区中只安装一次的 Mohist 管理入口。它负责建立工作区
连接、安装和管理 Agent 接入，但不代表任何一个业务 Agent，也不代替 Agent App 执行工作。

**Agent App**：一个 Agent 接入在 Slack 平台上的专属 App 与 Bot。它让一个 Mohist Agent
以独立身份出现在一个 Slack 工作区中，但不复制 Agent 定义；同一个 Mohist Agent 在不同
工作区中的 Agent App 是彼此独立的外部身份。

**Slack Bot**：Agent 接入在 Slack 中代表 Mohist Agent 的交互身份。Slack Bot 是客户端，
不是另一个 Mohist Agent，也不是外部 Agent。

**Web UI**：Mohist 的备用操作和可视化平面。它用于观察、人工操作和接管，不是用户
日常协作的工作站点，也不是主要交互入口；它仍可直接配置、启动和继续使用 Mohist Agent。

## Agent 执行

**Action**：工作所有者把一次执行交给 Runner 时使用的输入输出契约。Action 没有 Agent
身份，也不拥有工作生命周期。

**Inline Agent**：Workflow task 直接选择 Runtime Action 并提供输入的使用方式。它不是
持久化资源，没有 Agent ID。

**Agent 定义引用**：Workflow task 用 `uses: mohist/agent` 引用 Mohist Agent 定义的
使用方式。定义快照随 dispatch 解析；不创建 AgentJob，没有 Agent 身份。

**Mohist Agent**：Project 范围内可复用、具有稳定身份且可独立启动的预定义 Agent 资源。
Web UI、CLI、Agent 接入、事件路由和评论提及都只是它的不同入口。

**Agent Readiness**：Mohist 对 Agent 执行配置是否完整的统一诊断，取值为 `ready`、
`needs-setup` 或 `unknown`。它不是 active / archived 生命周期；`needs-setup` 必须带有可行动
的缺口，`unknown` 不能被入口猜成 Ready 或 Failed。

**Agent Availability**：当前是否有与 Agent 执行定义匹配的 Runner 可以立即执行，或工作
需要等待 Runner、容量或验证。Availability 是瞬时执行条件，不改变 Agent Readiness。

**AgentJob**：启动 Mohist Agent 时创建的一次工作。它拥有 launch 的调度状态、结果与恢复，
并关联 AgentSession 中的首个 AgentTurn；它不等同于持续对话，也不裁定后续 Follow-up。

**SessionInput**：AgentSession 已接受的一条有序输入。它有稳定身份，一个或多个连续
SessionInput 可以由同一个 AgentTurn 处理。

**AgentTurn**：AgentSession 中一次连续的 Runtime 处理过程。它包含一个或多个有序
SessionInput，并区分等待、执行和结果；它不是新的顶层工作。

**AgentSession**：Mohist 持有的稳定逻辑会话与审计记录。它按顺序拥有 SessionInput 与
AgentTurn，并持续记录回复和执行事实；一次 Turn 结束不会关闭 AgentSession。

避免用法：用“Session 完成”“Session 失败”或“Session closed”表达一次执行的结果。

**Activity**：AgentSession 当前是否有尚未终结的 Turn，取值为 `idle`、`active` 或
`unknown`。`active` 同时覆盖 Turn 排队和 Runtime 正在执行，具体阶段由 AgentTurn 状态表达；
Activity 不是一次工作的成功或失败结果。

**Runtime Session**：OpenCode、Pi 或其他执行后端拥有的物理对话。它可以被缓存、恢复、
替换或回收，不决定 AgentSession 是否还能接受后续输入。

**Runtime Binding**：AgentSession 当前关联的 Runtime、物理 Session 与 Runner 等路由
事实。Binding 可以整体替换，但不是 AgentSession 身份，也不是物理 Session 历史。

## Workflow 决策

**Approval**：对 Workflow 阶段产物作出的 approve 或 reject 决策。审批者署名是可选的
归属信息，不是决策成立的前提；未署名表示这次决策没有记录操作者。
