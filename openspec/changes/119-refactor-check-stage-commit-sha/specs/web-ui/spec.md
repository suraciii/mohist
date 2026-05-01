## ADDED Requirements

### Requirement: Web UI 展示 Check Suite 检查状态

Web UI Issue 详情页 SHALL 在 Check stage 展示当前 CheckSuite 的检查状态面板，实时显示每个检查项的 status（pending / running / passed / failed）。

#### Scenario: 实时显示检查项状态
- **WHEN** issue 处于 Check stage
- **AND** SSE 收到 `check_update` 事件
- **THEN** Check 面板更新对应检查项的状态指示器（如 spinner for running, checkmark for passed, x for failed）
- **AND** 不需要手动刷新页面

#### Scenario: CheckSuite 进入 awaiting-approval
- **WHEN** SSE 收到 `check_suite_status_changed` 事件
- **AND** suiteStatus 为 'awaiting-approval'
- **THEN** Check 面板显示所有检查项均为 passed 状态
- **AND** 审批面板自动出现

#### Scenario: 无活跃 CheckSuite
- **WHEN** issue 不处于 Check stage 或无活跃 CheckSuite
- **THEN** 不显示 Check 面板

#### Scenario: 检查循环重跑时面板更新
- **WHEN** SSE 收到 `check_update` 事件 with snapshotSha indicating reset
- **AND** 检查循环从头开始
- **THEN** Check 面板所有检查项重置为 pending
- **AND** 逐步更新为 running → passed/failed
