## Requirements

### Requirement: Issue 工作流状态机

Issue SHALL 按照定义的状态机流转。

#### Scenario: 完整工作流
- **WHEN** Issue 从 draft 开始
- **THEN** 按以下顺序流转：
  - draft → designing
  - designing → waiting-design-review
  - waiting-design-review → implementing (用户批准后)
  - implementing → waiting-review
  - waiting-review → done (用户批准，CLI 执行本地合并后)

#### Scenario: 用户可以暂停
- **WHEN** Issue 在任何阶段
- **AND** 用户执行 `mo issue pause <number>`
- **THEN** Issue 进入 paused 状态
- **AND** Engine 终止该 Issue 的运行中 Agent
- **AND** 该 Issue 的 running Task 被标记为 failed（reason: "user_paused"）
- **AND** Issue 状态保持 paused（不标记为 blocked）

#### Scenario: 用户可以恢复
- **WHEN** Issue 处于以下任一状态：
  - paused
  - blocked 且处于 Agent 阶段（designing 或 implementing）
  - active 且处于 Agent 阶段但无 pending/running 的 Task（Server 重启后）
- **AND** Issue 处于 Agent 阶段（designing 或 implementing）
- **AND** Issue 没有 running 或 pending 的 Task
- **AND** 用户执行 `mo issue resume <number>`
- **THEN** Issue 状态设为 active
- **AND** 为 Issue 的当前阶段创建新的 pending Task
- **AND** WorkflowEngine 自动 pick up 该 Task 并执行

#### Scenario: 恢复不适用于非 Agent 阶段
- **WHEN** 用户对以下状态的 Issue 执行 `mo issue resume <number>`
  - draft 阶段
  - waiting-design-review 或 waiting-review 阶段
  - done 阶段
- **THEN** 系统返回错误提示
- **AND** 不创建 Task

### Requirement: 用户在检查点介入

用户 SHALL 在关键检查点介入审查。

#### Scenario: 设计审查检查点
- **WHEN** designing 阶段完成
- **THEN** Issue 进入 waiting-design-review
- **AND** 等待用户执行 `mo issue approve <number>` 才能继续

#### Scenario: 实现审查检查点
- **WHEN** implementing 阶段完成
- **THEN** Issue 进入 waiting-review
- **AND** 等待用户执行 `mo issue approve <number>` 才能合并

### Requirement: 每个 Issue 对应一个工作分支

Issue SHALL 对应一个 git worktree 和分支（单分支模式）。

#### Scenario: 分支创建
- **WHEN** designing 阶段开始
- **THEN** Server 在 `Project.path` 下创建 git worktree 和分支 `mo/issue-{N}`
- **AND** 所有后续阶段在同一 worktree 中工作

#### Scenario: API 返回项目路径
- **WHEN** CLI 或系统查询 Issue 详情（`GET /api/issues/:number`）
- **THEN** 响应中包含 `projectPath` 字段（来自 `Project.path`）
- **AND** CLI 使用 `projectPath` 执行本地 git 操作（merge、diff）

#### Scenario: 分支更新
- **WHEN** implementing 阶段进行中
- **THEN** 代码变更提交到 `mo/issue-{N}` 分支

### Requirement: 合并后标记 Issue 完成

本地合并成功后 SHALL 自动标记 Issue 为 done。

#### Scenario: 本地合并
- **WHEN** 用户对 waiting-review 的 Issue 执行 approve
- **THEN** CLI 执行 `git merge --no-ff mo/issue-{N}`
- **AND** Issue 状态变为 done
- **AND** worktree 和分支被清理

### Requirement: Issue 操作基于当前项目

所有 Issue 操作 SHALL 基于当前项目上下文。

#### Scenario: 列出 Issues
- **WHEN** 用户执行 `mo issue list`
- **THEN** 返回当前项目的 Issues
- **AND** 显示当前项目名称

#### Scenario: 启动 Issue
- **WHEN** 用户执行 `mo issue start <number>`
- **THEN** 在当前项目的 repo 中创建 worktree 并启动处理
