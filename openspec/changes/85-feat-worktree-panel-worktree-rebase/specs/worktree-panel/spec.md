## ADDED Requirements

### Requirement: WorktreePanel 显示 worktree git 状态

IssueDetailPage 右侧 sidebar SHALL 在 MergeStatePanel 之后显示 WorktreePanel 组件，展示 worktree 的 git 状态信息。WorktreePanel 在 worktree 存在（stage 不是 Backlog/Explore，且 Done 阶段已合并的除外）时 SHALL 显示。

#### Scenario: worktree 存在时显示面板

- **WHEN** 用户查看 issue 详情页
- **AND** issue 对应的 worktree 存在（stage 为 plan/build/review/done，或 interrupted 状态且有 worktree）
- **THEN** sidebar 显示 WorktreePanel
- **AND** 面板标题为 "Worktree"

#### Scenario: worktree 不存在时隐藏面板

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `backlog` 或 `explore`
- **THEN** 不显示 WorktreePanel

#### Scenario: 显示分支名称

- **WHEN** WorktreePanel 显示
- **THEN** 面板显示当前分支名称（如 `mo/issue-85`）

#### Scenario: 显示 ahead/behind 状态 — up to date

- **WHEN** WorktreePanel 显示
- **AND** worktree-status 返回 `ahead: 0, behind: 0`
- **THEN** 面板显示 "Up to date" 指示（绿色）

#### Scenario: 显示 ahead/behind 状态 — behind master

- **WHEN** WorktreePanel 显示
- **AND** worktree-status 返回 `behind: 5`
- **THEN** 面板显示 "5 commits behind master"（橙色/警告色）
- **AND** Rebase 按钮高亮提示

#### Scenario: 显示 ahead/behind 状态 — ahead of master

- **WHEN** WorktreePanel 显示
- **AND** worktree-status 返回 `ahead: 3`
- **THEN** 面板显示 "3 commits ahead of master"

#### Scenario: 显示 ahead 和 behind 同时存在

- **WHEN** WorktreePanel 显示
- **AND** worktree-status 返回 `ahead: 2, behind: 4`
- **THEN** 面板显示 "2 ahead, 4 behind master"
- **AND** Rebase 按钮高亮提示

### Requirement: WorktreePanel 提供统一 Rebase 按钮

WorktreePanel SHALL 提供 "Rebase onto master" 按钮，替代所有 stage-specific 的 rebase 按钮。按钮行为根据 agent 运行状态分为直接执行和排队两种模式。

#### Scenario: agent 空闲时直接执行 rebase

- **WHEN** 用户点击 "Rebase onto master" 按钮
- **AND** agent 当前未运行（无 active agent session）
- **THEN** 按钮进入 loading 状态
- **AND** 发送 `POST /api/issues/:number/rebase` 请求
- **AND** 返回结果后显示 rebase 反馈（成功/冲突/已是最新）

#### Scenario: agent 运行中时排队 rebase

- **WHEN** 用户点击 rebase 按钮
- **AND** agent 当前正在运行
- **THEN** 按钮文字变为 "Rebase after completion"
- **AND** 发送 `POST /api/issues/:number/rebase?queue=true` 请求
- **AND** 返回 `{ queued: true }` 后显示 "Queued — will rebase when agent completes" 提示

#### Scenario: rebase 成功反馈

- **WHEN** rebase API 返回 `{ rebased: true }`
- **THEN** 面板显示 "Rebase successful" 提示（绿色）
- **AND** 自动刷新 worktree-status 和 issue 详情数据

#### Scenario: rebase 已是最新反馈

- **WHEN** rebase API 返回 `{ rebased: false, message: "Already up to date" }`
- **THEN** 面板显示 "Already up to date" 提示（蓝色信息色）

#### Scenario: rebase 冲突反馈

- **WHEN** rebase API 返回冲突
- **THEN** 面板显示 "Rebase aborted due to conflicts" 提示（红色）
- **AND** 展示冲突文件列表

#### Scenario: rebase 排队成功反馈

- **WHEN** rebase API 返回 `{ queued: true }`
- **THEN** 面板显示 "Queued" 状态指示（蓝色）
- **AND** agent 完成后自动触发 rebase
- **AND** rebase 执行后自动刷新状态

#### Scenario: rebase 进度通过 SSE 反馈

- **WHEN** SSE 收到 `rebase_started` 事件
- **THEN** 面板显示进度指示

- **WHEN** SSE 收到 `rebase_completed` 事件
- **THEN** 面板更新为完成状态
- **AND** 自动刷新 worktree-status 数据

- **WHEN** SSE 收到 `rebase_conflict` 事件
- **THEN** 面板显示冲突信息

#### Scenario: interrupted 状态可 rebase

- **WHEN** issue status 为 `interrupted`
- **AND** worktree 存在
- **THEN** WorktreePanel 显示并可执行 rebase
- **AND** agent 未运行，按钮为直接执行模式

### Requirement: 移除 stage-specific rebase 按钮

IssueDetailPage 中的所有 stage-specific rebase 按钮 SHALL 被移除，统一到 WorktreePanel。

#### Scenario: Build stage 不再显示独立 rebase 按钮

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `build`
- **THEN** Actions Panel 中不显示独立的 "Rebase onto master" 按钮
- **AND** Rebase 操作统一由 WorktreePanel 提供

#### Scenario: Plan approval gate 不再显示 rebase 按钮

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `plan` 且处于 approval gate
- **THEN** Approval Required 面板中不显示 rebase 按钮
- **AND** Rebase 操作统一由 WorktreePanel 提供

#### Scenario: Review approval gate 不再显示 rebase 按钮

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `review` 且处于 approval gate
- **THEN** Approval Required 面板中不显示 rebase 按钮
- **AND** Rebase 操作统一由 WorktreePanel 提供

### Requirement: MergeStatePanel 不变

Done 阶段的 MergeStatePanel 中 "Rebase and Retry" 按钮 SHALL 保持不变，不受 WorktreePanel 影响。

#### Scenario: Done stage MergeStatePanel 保留 Rebase and Retry

- **WHEN** issue stage 为 `done`
- **AND** mergeState 为 blocked/conflict/build-failed
- **THEN** MergeStatePanel 仍显示 "Rebase and Retry" 按钮
- **AND** 按钮行为不变（调用 mergeQueue retry）
