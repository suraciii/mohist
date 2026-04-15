## MODIFIED Requirements

### Requirement: Ralph-style task loop execution
The system SHALL execute tasks from prd.json in a loop, one at a time, until all are complete. 每个 task 的执行 SHALL 通过统一的 `runAcpSession()` 函数完成。

**Loop Driver:** Mohist Main-agent (not a single long-running coder process)

#### Scenario: Execute pending tasks sequentially
- **WHEN** the build stage starts
- **THEN** the main-agent reads prd.json
- **AND** identifies pending tasks (status: "pending")
- **AND** selects the task with lowest order/priority
- **AND** assembles complete context (proposal + design + spec + learnings)
- **AND** calls `runAcpSession({ cwd, task, issueId, projectId, executionId, eventBus })` with the assembled prompt
- **AND** waits for coder to complete
- **AND** verifies AC satisfaction
- **AND** updates task-status.json
- **AND** repeats until all tasks are complete

### Requirement: Task failure handling with retry
The system SHALL handle task failures with categorized retry logic.

**Failure Categories:**

| Type | Examples | Retry | Max Attempts |
|------|----------|-------|--------------|
| AC not met | Missing validation | Yes | 3 total |
| Environment | npm install failed | Yes | 2 total |
| Dependency | Can't find module | No | - |
| Timeout | >30min execution | No | - |

#### Scenario: Handle AC failure with retry
- **WHEN** task T-003 fails because AC "backend validation" not met
- **THEN** main-agent:
  1. Extracts failure reason: "Only frontend validation implemented"
  2. Stores learning with failure context
  3. If attempts < 3:
     - Assembles retry prompt with failure context
     - Calls runAcpSession again
  4. If attempts >= 3:
     - Pauses build
     - Asks user: retry, skip, or abort

#### Scenario: Handle non-retryable failure
- **WHEN** task fails due to "Cannot find auth module export"
- **AND** it's a dependency/code issue (not retryable)
- **THEN** main-agent immediately pauses
- **AND** asks user for guidance
- **AND** stores the dependency issue in learning

### Requirement: Task status persistence
The system SHALL persist task execution status for recovery.

**File:** `{change-path}/task-status.json`

```json
{
  "current_task_index": 3,
  "total_tasks": 7,
  "tasks": [
    {"id": "T-001", "status": "completed", "attempts": 1},
    {"id": "T-002", "status": "completed", "attempts": 1},
    {"id": "T-003", "status": "failed", "attempts": 3, "error": "Missing backend validation"}
  ]
}
```

#### Scenario: Resume from failed task
- **WHEN** user runs `mo issue resume` after build failure
- **THEN** main-agent reads task-status.json
- **AND** identifies current_task_index (3, meaning T-003)
- **AND** loads learnings from T-001 and T-002
- **AND** continues execution from T-003

### Requirement: Ralph loop 推送 task 进度事件

ralph executor SHALL 在 task 生命周期变化时通过 EventBus 推送 `ralph_task_update` 和 `ralph_loop_progress` 事件。

#### Scenario: task 开始时推送进度
- **WHEN** ralph executor 开始执行 task T-003（共 5 个 task）
- **THEN** EventBus emit `ralph_task_update`，status 为 `started`，taskIndex 为 2，totalTasks 为 5，executionId 为当前 tool call 的 executionId

#### Scenario: task 完成时推送进度
- **WHEN** task T-003 成功完成
- **THEN** EventBus emit `ralph_task_update`，status 为 `completed`
- **AND** EventBus emit `ralph_loop_progress`，completed 为已完成数，total 为总数

#### Scenario: task 失败重试时推送进度
- **WHEN** task T-003 失败，准备重试
- **THEN** EventBus emit `ralph_task_update`，status 为 `retrying`，attempt 为当前重试次数

#### Scenario: task 最终失败时推送进度
- **WHEN** task T-003 达到最大重试次数后仍失败
- **THEN** EventBus emit `ralph_task_update`，status 为 `failed`，error 包含失败原因

### Requirement: Ralph task 通过 runAcpSession 推送 agent 事件

ralph executor 的每个 task 执行 SHALL 通过 `runAcpSession` 推送 `coder_text_chunk` 和 `coder_tool_call` 事件。

#### Scenario: ralph task 内部 agent 输出文本
- **WHEN** ralph task 的 ACP session 收到 agent_message_chunk
- **AND** executionId 存在
- **THEN** EventBus emit `coder_text_chunk`，payload 包含 executionId 和文本 chunk

#### Scenario: ralph task 内部 agent 调用工具
- **WHEN** ralph task 的 ACP session 报告 tool_call 事件
- **AND** executionId 存在
- **THEN** EventBus emit `coder_tool_call`，payload 包含 executionId 和 toolName
