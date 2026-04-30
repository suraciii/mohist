## ADDED Requirements

### Requirement: BranchBar 组件显示分支同步状态

Issue 详情页左列 SHALL 在 Description 之前渲染 `BranchBar` 组件，显示当前 issue 的 worktree 分支名称、ahead/behind 状态和 baseBranch 名称。

#### Scenario: synced 状态（behind = 0）

- **WHEN** worktree 存在且 behind = 0
- **THEN** BranchBar 以透明背景显示
- **AND** 显示分支名（如 `mo/issue-26`）和 "up to date" 文本
- **AND** ahead 计数显示为 `↑N ahead`（N > 0 时显示，N = 0 时省略）
- **AND** 不显示 Rebase 按钮

#### Scenario: behind 状态（behind > 0）

- **WHEN** worktree 存在且 behind > 0
- **THEN** BranchBar 以 amber-50 背景显示
- **AND** 显示 `↑N ahead` 和 `↓M behind` 计数
- **AND** 显示 "Rebase onto <baseBranch>" 按钮

#### Scenario: rebase 进行中

- **WHEN** worktree 存在且 `rebaseInProgress` 为 true
- **THEN** BranchBar 以蓝色背景显示
- **AND** 显示进度指示器（spinner）和 "Rebasing..." 文本
- **AND** 不显示 Rebase 按钮

#### Scenario: rebase 冲突

- **WHEN** worktree 存在且 `rebaseInProgress` 为 true 且有 `conflictingFiles`
- **THEN** BranchBar 以红色强调显示
- **AND** 显示冲突文件列表

### Requirement: BranchBar 集中 rebase 操作

BranchBar 组件中的 Rebase 按钮 SHALL 调用已有的 `POST /api/issues/:number/rebase` 端点，复用现有 rebase mutation 逻辑。

#### Scenario: 点击 Rebase 按钮

- **WHEN** 用户点击 "Rebase onto <baseBranch>" 按钮
- **AND** agent 未在运行
- **THEN** 调用 `POST /api/issues/:number/rebase`
- **AND** 按钮变为 loading 状态
- **AND** rebase 完成后刷新 worktree status

#### Scenario: agent 运行中禁止 rebase

- **WHEN** agent 正在运行
- **THEN** Rebase 按钮显示为 disabled
- **AND** 显示提示 "Cannot rebase while agent is running"

#### Scenario: rebase 成功

- **WHEN** rebase API 返回成功
- **THEN** BranchBar 更新为 synced 状态
- **AND** 显示成功消息

#### Scenario: rebase 冲突

- **WHEN** rebase API 返回冲突
- **THEN** BranchBar 显示冲突文件列表
- **AND** 显示错误消息

### Requirement: BranchBar 按 stage 条件渲染

BranchBar SHALL 仅在 worktree 存在时显示，且仅在 Plan、Build、Review、Done stage 渲染。

#### Scenario: Plan stage 且 worktree 存在

- **WHEN** issue stage 为 `plan`
- **AND** worktree 存在
- **THEN** 显示 BranchBar

#### Scenario: Build stage 且 worktree 存在

- **WHEN** issue stage 为 `build`
- **AND** worktree 存在
- **THEN** 显示 BranchBar

#### Scenario: Backlog/Explore stage

- **WHEN** issue stage 为 `backlog` 或 `explore`
- **THEN** 不显示 BranchBar

#### Scenario: worktree 不存在

- **WHEN** issue 的 worktree 不存在（`exists: false`）
- **THEN** 不显示 BranchBar

### Requirement: BranchBar 自动刷新状态

BranchBar SHALL 定期轮询 worktree status 以反映最新同步状态。

#### Scenario: 定期轮询

- **WHEN** BranchBar 可见
- **THEN** 每 30 秒自动刷新 worktree status 数据

#### Scenario: rebase 完成后即时刷新

- **WHEN** rebase 操作完成（成功或失败）
- **THEN** 立即刷新 worktree status 数据

## MODIFIED Requirements

### Requirement: 移除无功能的 Skip 按钮

Issue 详情页的审批面板 SHALL 只保留可用操作。当前 Skip 按钮无后端支持，SHALL 被移除以避免误导用户。同时审批面板中的 rebase 按钮 SHALL 被移除，rebase 操作统一由 BranchBar 提供。

#### Scenario: 审批面板只显示可用操作

- **WHEN** 用户查看需要审批的 issue
- **THEN** 审批面板只显示 "Approve & Continue" 按钮
- **AND** 不显示无功能的 Skip 按钮
- **AND** 不显示 Rebase 按钮（rebase 由 BranchBar 提供）

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。审批面板 SHALL 仅包含审批决策（approve/reject），不包含 rebase 操作。

#### Scenario: agent 暂停后审批面板自动显示

- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮
- **AND** 审批面板不包含 rebase 按钮

#### Scenario: Issue 卡片状态实时更新

- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

### Requirement: Issue 详情页移除散落的 rebase 按钮

Issue 详情页 SHALL 移除 Actions panel Build stage 的独立 rebase 按钮、Plan approval gate 的 rebase 按钮和 Review approval gate 的 rebase 按钮。所有 rebase 逻辑统一由 BranchBar 组件处理。

#### Scenario: Build stage 不显示独立 rebase 按钮

- **WHEN** issue 处于 Build stage
- **THEN** Actions panel 不显示 "Rebase onto master" 按钮
- **AND** rebase 操作通过 BranchBar 提供

#### Scenario: Plan approval gate 不显示 rebase 按钮

- **WHEN** issue 处于 Plan stage 的 approval gate
- **THEN** 审批面板不包含 rebase 按钮
- **AND** 审批面板仅包含 approve/reject 决策按钮

#### Scenario: Review approval gate 不显示 rebase 按钮

- **WHEN** issue 处于 Review stage 的 approval gate
- **THEN** 审批面板不包含 rebase 按钮
- **AND** 审批面板仅包含 approve/reject 决策按钮
