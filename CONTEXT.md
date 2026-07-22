# Mohist 统一语言

本词汇表定义 Mohist 各上下文共享的 Agent 执行语言。生命周期、事件与模块边界的
规范见 [`design/agent-execution.md`](design/agent-execution.md)。

## Agent 执行

**Action**：工作所有者把一次执行交给 Runner 时使用的输入输出契约。Action 没有 Agent
身份，也不拥有工作生命周期。

**Inline Agent**：Workflow task 直接选择 Runtime Action 并提供输入的使用方式。它不是
持久化资源，没有 Agent ID。

**Mohist Agent**：Project 范围内可复用、具有稳定身份的预定义 Agent 资源。

**AgentJob**：Mohist Agent 的一次工作执行。它拥有本次工作的调度状态、结果与恢复，
但不等同于对话。

**AgentSession**：可以容纳多个 Turn 的稳定逻辑对话与审计记录。普通 Turn 完成、失败
或停止不会关闭 AgentSession。

避免用法：用“Session 完成”“Session 失败”或“Session closed”表达一个 Turn 的结果。

**Turn**：AgentSession 内一次有边界的对话执行，由 `(SessionId, TurnId)` 稳定标识。
Turn 从输入被受理并开始执行起存在，最终以 completed、failed 或 stopped 之一结束。

避免用法：用“Session”或“Job”代指 Turn。

**Runtime Session**：OpenCode、Pi 或其他执行后端拥有的物理对话。它可以被缓存、恢复、
替换或回收，不决定 AgentSession 是否还能接受后续输入。

**Runtime Binding**：AgentSession 当前关联的 Runtime、物理 Session、Runner 与工作目录
等路由事实。Binding 不是 AgentSession 身份。
