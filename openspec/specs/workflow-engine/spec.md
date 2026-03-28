## Requirements

### Requirement: WorkflowEngine 以多 Worker 模式运行

WorkflowEngine SHALL 启动 N 个独立 Worker，每个 Worker 独立循环从 TaskRepo 取任务并执行。

#### Scenario: 启动 Engine

- **WHEN** Server 启动
- **THEN** WorkflowEngine 创建 `maxConcurrentAgents`（默认 8）个 Worker
- **AND** 每个 Worker 开始独立轮询 TaskRepo

#### Scenario: Worker 原子取任务

- **WHEN** Worker 从 TaskRepo 取任务
- **THEN** 使用 findAndClaim 原子操作（单条 UPDATE status='running' + SELECT in one SQLite transaction）
- **AND** SQL 查询包含 same-Issue 约束：`WHERE status='pending' AND NOT EXISTS (SELECT 1 FROM tasks t2 WHERE t2.issue_id = t.issue_id AND t2.status = 'running')`
- **AND** 确保每个 pending Task 只被一个 Worker 拿到
- **AND** 同一 Issue 已有 running Task 时，其 pending Task 不会被选中
- **AND** 多个 Worker 并发调用不会取到同一个 Task

#### Scenario: Worker 无任务可取

- **WHEN** Worker 轮询 TaskRepo 但没有 pending 的 Task
- **THEN** Worker 等待 pollInterval（默认 2 秒）后重试

### Requirement: WorkflowEngine 执行完成后流转 Issue 阶段

WorkflowEngine SHALL 在 Task 执行成功后将 Issue 流转到下一阶段。

#### Scenario: 执行成功

- **WHEN** StageHandler 执行成功
- **THEN** Task 状态更新为 completed
- **AND** Issue 流转到下一阶段
- **AND** 如果下一阶段需要用户审批（waiting-design-review、waiting-review），不创建新 Task
- **AND** 如果下一阶段是 Agent 阶段，创建新 Task 并入队

#### Scenario: 执行成功且到达 Done

- **WHEN** StageHandler 执行成功且下一阶段为 Done
- **THEN** Task 状态更新为 completed
- **AND** Issue 流转到 Done
- **AND** 不创建新 Task

### Requirement: WorkflowEngine 执行失败时标记 Issue 为 blocked

WorkflowEngine SHALL 在 Task 执行失败时将 Issue 标记为 blocked，除非 Issue 已被用户暂停。

#### Scenario: Agent 执行失败

- **WHEN** StageHandler 执行抛出异常
- **AND** Issue 状态为 active
- **THEN** Task 状态更新为 failed 并记录错误信息
- **AND** Issue 状态更新为 blocked
- **AND** 不创建新 Task
- **AND** 不自动重试

#### Scenario: Agent 被 pause 终止

- **WHEN** Engine 通过 killAgentByIssueId() 终止 Agent（pause 触发）
- **THEN** Task 已被标记为 failed（reason: "user_paused"）
- **AND** Issue 保持 paused 状态（不标记为 blocked）
- **AND** Worker 检测到 Task 已是 failed，幂等跳过

### Requirement: WorkflowEngine 确保同一 Issue 同时只有一个 Task 执行

WorkflowEngine SHALL 防止同一 Issue 的多个 Task 并行执行。

#### Scenario: 同一 Issue 已有 running 的 Task

- **WHEN** 同一 Issue 有多个 pending 的 Task（如前一个 failed 后新建的）
- **AND** 该 Issue 已有一个 status 为 running 的 Task
- **THEN** findAndClaim 不会选中该 Issue 的任何 pending Task
- **AND** 这些 pending Task 保持 pending 状态直到 running Task 完成

### Requirement: WorkflowEngine 支持优雅停止

WorkflowEngine SHALL 支持优雅停止，等待当前任务完成。

#### Scenario: 优雅停止

- **WHEN** 收到停止信号
- **THEN** WorkflowEngine 停止所有 Worker 取新任务
- **AND** 等待当前运行中的 Task 完成（最多 30 秒）
- **AND** 超时后强制终止所有 Agent 子进程

#### Scenario: 强制终止

- **WHEN** 优雅停止超时
- **THEN** WorkflowEngine 终止所有运行中的 Agent 子进程
- **AND** 将所有 running 的 Task 标记为 failed

### Requirement: WorkflowEngine 替换内存 TaskQueue

WorkflowEngine SHALL 直接使用 TaskRepo 进行任务管理，不再使用内存 TaskQueue。

#### Scenario: 任务入队

- **WHEN** API 路由需要创建任务
- **THEN** 通过 StateManager.createTask() 创建 Task 并持久化到 TaskRepo
- **AND** 不使用内存 TaskQueue

#### Scenario: 服务器重启后

- **WHEN** 服务器重启
- **THEN** recoverState() 将所有 running 的 Task 标记为 failed
- **AND** WorkflowEngine 启动后只处理 pending 的 Task

### Requirement: WorkflowEngine 支持按 Issue 终止 Agent

WorkflowEngine SHALL 支持通过 Issue ID 终止对应的运行中 Agent。

#### Scenario: 终止指定 Issue 的 Agent

- **WHEN** 外部调用 `killAgentByIssueId(issueId)`
- **THEN** Engine 找到该 Issue 的 running Task
- **AND** 将 Task 标记为 failed（reason: "user_paused"）
- **AND** 终止对应的 Agent 子进程
- **AND** Worker 的 Promise reject 后发现 Task 已是 failed，幂等跳过

### Requirement: advance_stage tool enforces M1 stage transition whitelist
The advance_stage tool SHALL only allow stage transitions defined in the M1 whitelist: `draft→designing`, `designing→implementing`, `implementing→done`. All other transitions SHALL be rejected with an error message listing allowed transitions from the current stage.

#### Scenario: Valid M1 transition
- **WHEN** LLM calls advance_stage with issue in stage "designing" and target stage "implementing"
- **THEN** the issue stage SHALL be updated to "implementing"
- **AND** a success message SHALL be returned

#### Scenario: Invalid transition — skip stage
- **WHEN** LLM calls advance_stage with issue in stage "designing" and target stage "done"
- **THEN** the transition SHALL be rejected
- **AND** an error message SHALL be returned listing allowed transitions from "designing"

#### Scenario: Invalid transition — backward
- **WHEN** LLM calls advance_stage with issue in stage "implementing" and target stage "draft"
- **THEN** the transition SHALL be rejected
- **AND** an error message SHALL be returned

#### Scenario: Invalid transition — to waiting stage
- **WHEN** LLM calls advance_stage with issue in stage "designing" and target stage "waiting-design-review"
- **THEN** the transition SHALL be rejected
- **AND** an error message SHALL be returned
