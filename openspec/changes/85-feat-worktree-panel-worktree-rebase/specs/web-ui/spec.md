## ADDED Requirements

### Requirement: 前端提供 useWorktreeStatus hook

`useQueries.ts` SHALL 新增 `useWorktreeStatus` hook，封装 worktree 状态查询。

#### Scenario: 调用 useWorktreeStatus

- **WHEN** 组件调用 `useWorktreeStatus(issueNumber)`
- **THEN** 发起 `GET /api/issues/:issueNumber/worktree-status` 请求
- **AND** 返回 `{ exists, branch, ahead, behind, canFastForward, isRebaseInProgress }` 数据
- **AND** 使用 React Query，queryKey 为 `['worktree-status', issueNumber]`

#### Scenario: issue 不存在时

- **WHEN** 调用 `useWorktreeStatus(issueNumber)`
- **AND** API 返回 404
- **THEN** 返回 error 状态

### Requirement: 前端 API client 提供 worktree-status 和 queue rebase 方法

`api.ts` SHALL 提供 `getWorktreeStatus` 方法和扩展 `rebaseIssue` 方法支持 queue 参数。

#### Scenario: getWorktreeStatus 调用

- **WHEN** 调用 `api.getWorktreeStatus(85)`
- **THEN** 发送 `GET /api/issues/85/worktree-status` 请求
- **AND** 返回 worktree 状态对象

#### Scenario: rebaseIssue with queue

- **WHEN** 调用 `api.rebaseIssue(85, { queue: true })`
- **THEN** 发送 `POST /api/issues/85/rebase?queue=true` 请求
- **AND** 返回 rebase 结果（直接执行或排队）

### Requirement: WorktreePanel 显示在 sidebar

IssueDetailPage sidebar SHALL 在 MergeStatePanel 之后、Review Report 之前渲染 WorktreePanel 组件。

#### Scenario: WorktreePanel 在 sidebar 中的位置

- **WHEN** 用户查看 issue 详情页
- **AND** worktree 存在
- **THEN** sidebar 中面板顺序为：Details → Pipeline Interrupted → Actions → MergeStatePanel → **WorktreePanel** → Review Report → Approval Required

## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。

#### Scenario: agent 暂停后审批面板自动显示
- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮

#### Scenario: Issue 卡片状态实时更新
- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

#### Scenario: Rebase 进度实时显示
- **WHEN** SSE 收到 `rebase_started`、`rebase_progress`、`rebase_completed` 或 `rebase_conflict` 事件
- **THEN** WorktreePanel 显示对应进度状态
- **AND** `rebase_completed` 事件后自动刷新 issue 详情数据和 worktree-status

#### Scenario: agent 完成后触发排队 rebase
- **WHEN** SSE 收到 `agent_completed` 事件
- **AND** WorktreePanel 有排队中的 rebase 请求
- **THEN** 自动触发 `POST /api/issues/:number/rebase`
- **AND** WorktreePanel 显示 rebase 进度

## REMOVED Requirements

### Requirement: IssueDetailPage 显示 Rebase 按钮
**Reason**: 所有 stage-specific rebase 按钮统一到 WorktreePanel（新 worktree-panel capability）
**Migration**: WorktreePanel 组件在 sidebar 中提供统一的 rebase 操作，覆盖所有 stage（包括 interrupted 状态）
