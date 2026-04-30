## ADDED Requirements

### Requirement: API 提供 worktree status 端点

Server SHALL 提供 `GET /api/issues/:number/worktree-status` 端点，返回 issue 对应 worktree 分支相对于 baseBranch 的同步状态。

#### Scenario: 获取 worktree 同步状态

- **WHEN** 请求 `GET /api/issues/:number/worktree-status`
- **AND** issue 存在且 worktree 存在
- **THEN** 执行 `git rev-list --left-right --count origin/<baseBranch>...mo/issue-<number>` 获取 ahead/behind 计数
- **AND** 检查 worktree 是否处于 rebase 进行中状态
- **AND** 返回 `{ success: true, data: { branch, baseBranch, ahead, behind, rebaseInProgress, exists: true } }`

#### Scenario: worktree 不存在

- **WHEN** 请求 `GET /api/issues/:number/worktree-status`
- **AND** issue 存在但 worktree 不存在
- **THEN** 返回 `{ success: true, data: { exists: false } }`

#### Scenario: issue 不存在

- **WHEN** 请求 `GET /api/issues/:number/worktree-status`
- **AND** issue 不存在
- **THEN** 返回 404 错误

#### Scenario: 无 project 上下文

- **WHEN** 请求 `GET /api/issues/:number/worktree-status`
- **AND** 无当前 project 上下文
- **THEN** 返回 400 错误

#### Scenario: rebase 进行中

- **WHEN** 请求 `GET /api/issues/:number/worktree-status`
- **AND** worktree 存在且 rebase 正在进行中
- **THEN** 返回 `{ success: true, data: { branch, baseBranch, ahead, behind, rebaseInProgress: true, exists: true, conflictingFiles?: string[] } }`
- **AND** 如果有冲突文件，`conflictingFiles` 包含冲突文件列表

### Requirement: 前端 API client 提供 getWorktreeStatus 方法

`api.ts` SHALL 添加 `getWorktreeStatus` 方法，对应后端 `GET /api/issues/:number/worktree-status`。

#### Scenario: getWorktreeStatus 调用

- **WHEN** 调用 `api.getWorktreeStatus(number)`
- **THEN** 发送 `GET /api/issues/:number/worktree-status` 请求
- **AND** 返回 worktree status 数据对象

### Requirement: 前端 hooks 提供 useWorktreeStatus

`useQueries.ts` SHALL 提供 `useWorktreeStatus` hook，封装 `GET /api/issues/:number/worktree-status` 请求。

#### Scenario: 调用 useWorktreeStatus

- **WHEN** 组件调用 `useWorktreeStatus(issueNumber)`
- **THEN** 发起 `GET /api/issues/:number/worktree-status` 请求
- **AND** 仅当 issueNumber 有效时 enabled

#### Scenario: issueNumber 为空或 undefined

- **WHEN** 组件调用 `useWorktreeStatus(undefined)` 或 `useWorktreeStatus(0)`
- **THEN** query 的 `enabled` 为 false，不发送请求
