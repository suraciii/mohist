## REMOVED Requirements

### Requirement: WorkflowEngine 以多 Worker 模式运行
**Reason**: The old WorkflowEngine has been replaced by the M1 agent architecture (Main Agent + spawn_agent tool). No M1 code path uses WorkflowEngine.
**Migration**: The M1 agent loop in `agents/main-agent.ts` handles issue processing.

### Requirement: WorkflowEngine 执行完成后流转 Issue 阶段
**Reason**: Stage transitions are now handled by the `advance_stage` tool called by the Main Agent LLM, not by the WorkflowEngine.
**Migration**: The `advance_stage` tool enforces M1 transitions via `M1_ALLOWED_TRANSITIONS`.

### Requirement: WorkflowEngine 执行失败时标记 Issue 为 blocked
**Reason**: Error handling is now in `api/issues.ts` start endpoint — the agent loop catch block sets `IssueStatus.Blocked`.
**Migration**: Error handling is inline in the start endpoint.

### Requirement: WorkflowEngine 确保同一 Issue 同时只有一个 Task 执行
**Reason**: M1 is single-issue (D12 from agent-architecture design). The start endpoint rejects if an agent is already running.
**Migration**: The `activeAgentPromise` check in the start endpoint enforces this.

### Requirement: WorkflowEngine 支持优雅停止
**Reason**: M1 has no WorkflowEngine. Server shutdown is handled by the HTTP server's stop method.
**Migration**: SIGTERM/SIGINT handlers in `server/index.ts` call `server.stop()`.

### Requirement: WorkflowEngine 替换内存 TaskQueue
**Reason**: M1 has no task queue. The agent loop is started directly by the start endpoint.
**Migration**: No migration needed.

### Requirement: WorkflowEngine 支持按 Issue 终止 Agent
**Reason**: M1 has no WorkflowEngine. Agent termination requires stopping the server process.
**Migration**: No migration needed. M2 will re-implement pause with AbortController.

### Requirement: advance_stage tool enforces M1 stage transition whitelist
**Reason**: This requirement is stable and already implemented. Moving it from a delta to a permanent record.
**Migration**: No migration needed. The implementation in `tools/advance-stage.ts` remains.

## ADDED Requirements

### Requirement: Stage enum contains only M1 stages
The `Stage` enum SHALL contain only values used by M1: `Draft`, `Designing`, `Implementing`, `Done`. The values `WaitingDesignReview` and `WaitingReview` SHALL be removed.

#### Scenario: Stage enum values
- **WHEN** the Stage enum is inspected
- **THEN** it SHALL contain exactly 4 values: `draft`, `designing`, `implementing`, `done`
- **AND** it SHALL NOT contain `waiting-design-review` or `waiting-review`

### Requirement: Task infrastructure is removed
The `Task` interface SHALL be removed from `types/index.ts`. The `TaskRepo` class SHALL be deleted. The `tasks` SQLite table SHALL be dropped.

#### Scenario: No Task type
- **WHEN** the types module is inspected
- **THEN** it SHALL NOT export a `Task` interface

#### Scenario: No TaskRepo
- **WHEN** the db module is inspected
- **THEN** it SHALL NOT export `TaskRepo`

#### Scenario: Tasks table dropped
- **WHEN** the server starts and initializes the database
- **THEN** the `tasks` table SHALL NOT exist
