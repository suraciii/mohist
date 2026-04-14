## MODIFIED Requirements

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作，基于 Hono 框架实现。API handler SHALL 通过 IssueService 操作 issue 数据，不直接调用 StateManager 的 CRUD 方法。

#### Scenario: 创建 Issue

- **WHEN** CLI 请求 `POST /api/issues` with `{ title, body?, labels? }`
- **THEN** 通过 IssueService 创建 Issue
- **AND** 返回 Issue 信息

#### Scenario: 更新 Issue

- **WHEN** CLI 请求 `PATCH /api/issues/:number` with `{ title?, body?, addLabels?, removeLabels? }`
- **THEN** 通过 IssueService 更新 Issue
- **AND** 返回更新后的 Issue

#### Scenario: 添加评论

- **WHEN** CLI 请求 `POST /api/issues/:number/comments` with `{ body }`
- **THEN** 通过 IssueService 创建 comment
- **AND** 返回 comment 信息

#### Scenario: 启动 Issue 处理

- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue 处于 `Draft` stage
- **THEN** 系统先创建 worktree（如果需要）
- **AND** worktree 创建成功后，将 issue stage 转换为 `Plan`
- **AND** 启动 Main Agent 处理该 Issue
- **AND** 返回 Issue 信息和运行状态

#### Scenario: 启动 Issue 时 worktree 创建失败

- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue 处于 `Draft` stage
- **AND** worktree 创建失败（网络错误、分支不存在等）
- **THEN** 系统 SHALL NOT 修改 issue 的 stage
- **AND** issue 保持 `Draft` stage
- **AND** 返回 500 错误，包含失败原因
- **AND** 用户可以直接重新执行 `mo issue start <number>`

#### Scenario: 启动 Issue 时 worktree 创建成功但 agentRunner 未配置

- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue 处于 `Draft` stage
- **AND** worktree 创建成功
- **AND** `agentRunner` 未配置（null/undefined）
- **THEN** 系统 SHALL NOT 修改 issue 的 stage
- **AND** issue 保持 `Draft` stage
- **AND** 返回 500 错误，错误信息包含 "AgentRunnerService not configured"

#### Scenario: 启动 Issue 时 stage 转换后 agent 启动失败

- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue 处于 `Draft` stage
- **AND** worktree 创建成功
- **AND** issue stage 已转换为 `Plan`
- **AND** 后续步骤抛出异常
- **THEN** 系统 SHALL 将 issue stage rollback 为 `Draft`
- **AND** 返回 500 错误，包含失败原因
- **AND** 保留已创建的 worktree（幂等，下次 start 会复用）

#### Scenario: rollback stage 失败

- **WHEN** 启动 Issue 流程 catch 块尝试 rollback stage 为 `Draft`
- **AND** rollback 本身失败（如数据库锁定）
- **THEN** 系统记录 rollback 失败的 error 日志
- **AND** 仍然返回原始错误（不吞掉原始错误信息）
