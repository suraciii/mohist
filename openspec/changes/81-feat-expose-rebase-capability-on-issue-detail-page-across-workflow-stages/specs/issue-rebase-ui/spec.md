## ADDED Requirements

### Requirement: IssueDetailPage 显示 Rebase 按钮

IssueDetailPage 的 Actions Panel SHALL 在 plan、build、review stage 显示 "Rebase onto master" 按钮，供用户主动同步分支到最新 master。

#### Scenario: Plan stage 显示 Rebase 按钮

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `plan`
- **AND** agent 已暂停（awaiting approval）
- **THEN** Actions Panel 在 Approve 按钮下方显示 "Rebase onto master" 按钮（outline style）

#### Scenario: Build stage agent 空闲时显示 Rebase 按钮

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `build`
- **AND** agent 未运行（idle）
- **THEN** Actions Panel 显示 "Rebase onto master" 按钮

#### Scenario: Build stage agent 运行时禁用 Rebase 按钮

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `build`
- **AND** agent 正在运行
- **THEN** "Rebase onto master" 按钮显示为 disabled 状态
- **AND** tooltip 显示 "Cannot rebase while agent is running"

#### Scenario: Review stage 显示 Rebase 按钮

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `review`
- **AND** agent 已暂停（awaiting approval）
- **THEN** Actions Panel 在 Approve 按钮上方显示 "Rebase onto master" 按钮
- **AND** 按钮旁显示提示文字 "Rebase before review for latest diff"

#### Scenario: Backlog/Explore stage 不显示 Rebase 按钮

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `backlog` 或 `explore`
- **THEN** 不显示 "Rebase onto master" 按钮

#### Scenario: Done stage 不显示 Rebase 按钮

- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 为 `done`
- **THEN** 不显示 "Rebase onto master" 按钮
- **AND** MergeStatePanel 通过 "Rebase and Retry" 按钮提供等效功能

### Requirement: Rebase 按钮触发 API 调用

点击 "Rebase onto master" 按钮 SHALL 调用 `POST /api/issues/:number/rebase`。

#### Scenario: 点击 Rebase 按钮

- **WHEN** 用户点击 "Rebase onto master" 按钮
- **THEN** 按钮进入 loading 状态（disabled + spinner）
- **AND** 发送 `POST /api/issues/:number/rebase` 请求

#### Scenario: Rebase 成功后刷新页面

- **WHEN** rebase API 返回 200 且 `rebased: true`
- **THEN** 显示成功提示 "Rebase successful"
- **AND** 刷新 issue 详情数据

#### Scenario: Rebase 已是最新

- **WHEN** rebase API 返回 200 且 `rebased: false`
- **THEN** 显示信息提示 "Already up to date"

#### Scenario: Rebase 有冲突

- **WHEN** rebase API 返回 409 且包含 conflicts 列表
- **THEN** 显示错误提示 "Rebase aborted due to conflicts"
- **AND** 展示冲突文件列表

### Requirement: MergeStatePanel 重命名 Retry Merge 按钮

MergeStatePanel 中的 "Retry Merge" 按钮 SHALL 重命名为 "Rebase and Retry"。

#### Scenario: Done stage merge blocked 时显示 Rebase and Retry

- **WHEN** issue stage 为 `done`
- **AND** mergeState 为 `blocked`
- **THEN** MergeStatePanel 显示 "Rebase and Retry" 按钮
- **AND** 不再显示 "Retry Merge" 文字

### Requirement: Rebase 进度通过 SSE 反馈

Web UI SHALL 监听 rebase 相关 SSE 事件，展示 rebase 进度。

#### Scenario: 收到 rebase_started 事件

- **WHEN** SSE 收到 `rebase_started` 事件
- **THEN** Rebase 按钮区域显示进度指示 "Checking fast-forward..."

#### Scenario: 收到 rebase_progress 事件

- **WHEN** SSE 收到 `rebase_progress` 事件，payload 包含 `{ step: "rebasing" }`
- **THEN** 进度指示更新为 "Rebasing..."

- **WHEN** SSE 收到 `rebase_progress` 事件，payload 包含 `{ step: "verifying" }`
- **THEN** 进度指示更新为 "Build verifying..."

#### Scenario: 收到 rebase_completed 事件

- **WHEN** SSE 收到 `rebase_completed` 事件
- **THEN** 进度指示更新为 "Rebase complete"
- **AND** 刷新 issue 详情数据

#### Scenario: 收到 rebase_conflict 事件

- **WHEN** SSE 收到 `rebase_conflict` 事件，payload 包含 `{ conflicts: string[] }`
- **THEN** 显示冲突文件列表
- **AND** 显示建议操作提示
