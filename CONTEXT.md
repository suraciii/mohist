# Mohist 统一语言

本词汇表定义 Mohist 各上下文共享的 Agent 执行语言。生命周期、事件与模块边界的
规范见 [`design/agent-execution.md`](design/agent-execution.md)。

## Agent 执行

**Action**：工作所有者把一次执行交给 Runner 时使用的输入输出契约。Action 没有 Agent
身份，也不拥有工作生命周期。

**Inline Agent**：Workflow task 直接选择 Runtime Action 并提供输入的使用方式。它不是
持久化资源，没有 Agent ID。

**Agent 定义引用**：Workflow task 用 `uses: mohist/agent` 引用 Mohist Agent 定义的
使用方式。定义快照随 dispatch 解析；不创建 AgentJob，没有 Agent 身份。

**Mohist Agent**：Project 范围内可复用、具有稳定身份的预定义 Agent 资源。

**AgentJob**：Mohist Agent 的一次工作执行。它拥有本次工作的调度状态、结果与恢复，
但不等同于对话。

**AgentSession**：Mohist 持有的稳定逻辑会话与审计记录。它持续记录输入、回复和执行
事实；一次执行结束不会关闭 AgentSession。

避免用法：用“Session 完成”“Session 失败”或“Session closed”表达一次执行的结果。

**Activity**：AgentSession 当前是否仍在处理输入的会话状态，取值为 `idle`、`active`
或 `unknown`。Activity 不是一次工作的成功或失败结果。

**Runtime Session**：OpenCode、Pi 或其他执行后端拥有的物理对话。它可以被缓存、恢复、
替换或回收，不决定 AgentSession 是否还能接受后续输入。

**Runtime Binding**：AgentSession 当前关联的 Runtime、物理 Session 与 Runner 等路由
事实。Binding 可以整体替换，但不是 AgentSession 身份，也不是物理 Session 历史。
