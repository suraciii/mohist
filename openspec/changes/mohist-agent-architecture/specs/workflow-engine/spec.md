## REMOVED Requirements

### Requirement: WorkflowEngine 以多 Worker 模式运行
**Reason**: Replaced by agent-driven workflow. The WorkflowEngine with multi-worker polling is removed entirely. The Main Agent (Workflow Agent) now handles orchestration, spawning sub-agents directly instead of using a task queue.
**Migration**: Remove `workflow/engine.ts`. Issue orchestration is now handled by the Workflow Agent (Main Agent) with per-issue sessions.

### Requirement: WorkflowEngine 执行完成后流转 Issue 阶段
**Reason**: Stage transitions are now driven by the Main Agent's LLM decisions, not by a deterministic engine. The Main Agent evaluates sub-agent output and decides whether to advance.
**Migration**: Stage transitions are handled by the Main Agent calling `advance_stage` tool.

### Requirement: WorkflowEngine 执行失败时标记 Issue 为 blocked
**Reason**: Error handling is now intelligent (LLM-driven). The Main Agent analyzes failures and decides: retry, ask user, or mark blocked.
**Migration**: Failure handling is now the Main Agent's responsibility via LLM decision-making.

### Requirement: WorkflowEngine 确保同一 Issue 同时只有一个 Task 执行
**Reason**: No longer needed. Each issue has exactly one Main Agent session that runs sequentially. Sub-agents are spawned one at a time and waited for synchronously.
**Migration**: Sequential execution is guaranteed by the single Main Agent session per issue.

### Requirement: WorkflowEngine 支持优雅停止
**Reason**: Server shutdown now handles active Main Agent sessions by persisting their state to SQLite. On restart, sessions are restored.
**Migration**: Session persistence replaces graceful shutdown of workers.

### Requirement: WorkflowEngine 替换内存 TaskQueue
**Reason**: No longer needed. The task queue pattern is replaced by the Main Agent's direct sub-agent spawning.
**Migration**: Remove TaskRepo and task-related database tables.

### Requirement: WorkflowEngine 支持按 Issue 终止 Agent
**Reason**: Sub-agent termination is now handled by the Main Agent's abort mechanism (AbortController), not by the engine's killAgentByIssueId.
**Migration**: Sub-agent cancellation uses AbortController signals.
