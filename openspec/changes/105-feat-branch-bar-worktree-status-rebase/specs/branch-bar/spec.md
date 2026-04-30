## ADDED Requirements

### Requirement: BranchBar 组件提供统一的分支状态可视化

`BranchBar` 组件 SHALL 作为 issue 详情页左列的最顶层 context 元素，位于 Description 之前，显示 worktree 分支名称、ahead/behind 同步计数、baseBranch 名称，并提供集中的 rebase 操作入口。

#### Scenario: 组件基本结构

- **WHEN** BranchBar 渲染
- **AND** worktree 存在
- **THEN** 显示分支名（`mo/issue-{N}` 格式）
- **AND** 显示 ahead 计数（`↑N ahead`）
- **AND** 显示 behind 计数（`↓N behind`）
- **AND** 显示 baseBranch 名称

#### Scenario: 三种状态变体

- **WHEN** behind = 0 且非 rebasing
- **THEN** 使用 synced 变体：透明背景，绿色状态文本 "up to date"，无 Rebase 按钮
- **WHEN** behind > 0 且非 rebasing
- **THEN** 使用 behind 变体：amber-50 背景，amber 强调色，显示 "Rebase onto {baseBranch}" 按钮
- **WHEN** rebaseInProgress = true
- **THEN** 使用 rebasing 变体：蓝色背景，spinner，"Rebasing..." 文本，不显示 Rebase 按钮

### Requirement: BranchBar 通过 useWorktreeStatus hook 获取数据

BranchBar SHALL 使用 `useWorktreeStatus` hook 获取分支同步状态数据，通过 `useMutation` 调用已有的 `POST /api/issues/:number/rebase` 端点执行 rebase 操作。

#### Scenario: 数据获取

- **WHEN** BranchBar 挂载
- **THEN** 通过 `useWorktreeStatus(issueNumber)` 获取 status 数据
- **AND** `useWorktreeStatus` 调用 `GET /api/issues/:number/worktree-status`

#### Scenario: Rebase mutation

- **WHEN** 用户点击 Rebase 按钮
- **THEN** 调用 `api.rebaseIssue(issueNumber)`（现有 `POST /api/issues/:number/rebase`）
- **AND** mutation 完成后使 worktree status query 失效并重新获取

### Requirement: BranchBar 放置在左列主内容区

BranchBar SHALL 渲染在 issue 详情页两栏布局的左列（`lg:col-span-2`），位于 Description section 之前，而非右侧 sidebar。

#### Scenario: 左列放置

- **WHEN** issue 详情页渲染
- **THEN** BranchBar 出现在 grid 布局的左列
- **AND** BranchBar 位于 Description section 的正上方
- **AND** BranchBar 不出现在右侧 sidebar 中
