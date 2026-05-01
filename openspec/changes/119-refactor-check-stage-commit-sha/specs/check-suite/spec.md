## ADDED Requirements

### Requirement: Check Suite 数据模型

系统 SHALL 为每个 issue 的 Check stage 维护一个 CheckSuite 数据模型，持久化到 SQLite `check_suites` 表。

```
CheckSuite {
  id: string (UUID)
  issueId: string (FK → issues.id)
  snapshotSha: string (40-char hex)
  status: 'running' | 'awaiting-approval' | 'passed' | 'failed'
  checks: {
    build-test: CheckState
    ai-review: CheckState
  }
  createdAt: string (ISO 8601)
  updatedAt: string (ISO 8601)
}

CheckState {
  status: 'pending' | 'running' | 'passed' | 'failed'
  output?: any
  ranAt?: string (ISO 8601)
}
```

每个 issue 同一时刻 SHALL 只有一个活跃的 CheckSuite（status 为 running 或 awaiting-approval）。

#### Scenario: Check stage 进入时创建 CheckSuite
- **WHEN** issue 进入 Check stage
- **THEN** 系统创建新 CheckSuite，snapshotSha = 当前 worktree HEAD SHA
- **AND** status = 'running'
- **AND** 所有检查项初始 CheckState.status = 'pending'

#### Scenario: 同一 issue 只有一个活跃 CheckSuite
- **WHEN** issue 已有 status 为 'running' 的 CheckSuite
- **THEN** 系统不复创建新 CheckSuite
- **AND** 重用现有记录

### Requirement: Check Suite snapshotSha 快照

系统 SHALL 在 Check stage 进入时记录 worktree 的 HEAD commit SHA 作为 snapshotSha。auto-fix 产生新 commit 后 SHALL 更新 snapshotSha 并重置所有检查项为 pending。

#### Scenario: 进入 Check stage 记录 snapshotSha
- **WHEN** Check stage 开始执行
- **THEN** 读取 worktree HEAD SHA 并存储为 CheckSuite.snapshotSha

#### Scenario: auto-fix 产生新 commit 后更新 snapshotSha
- **WHEN** build-test 或 ai-review 检查失败
- **AND** auto-fix 成功产生新 commit
- **THEN** CheckSuite.snapshotSha 更新为新 commit SHA
- **AND** 所有检查项 CheckState 重置为 pending
- **AND** 检查循环从头开始

### Requirement: Check Suite 检查结果持久化

系统 SHALL 在每个检查项执行完成后将其结果持久化到 CheckSuite.checks 中，包含 status、output 和 ranAt。

#### Scenario: 检查项完成后持久化结果
- **WHEN** build-test 检查执行完成（pass 或 fail）
- **THEN** CheckSuite.checks['build-test'] 更新为 `{ status: result.status, output: result.output, ranAt: ISO 8601 }`
- **AND** CheckSuite.updatedAt 更新

#### Scenario: 检查项开始执行时标记 running
- **WHEN** build-test 检查开始执行
- **THEN** CheckSuite.checks['build-test'].status 更新为 'running'
- **AND** 持久化到 DB

#### Scenario: 所有检查通过后 suite 状态变更
- **WHEN** 所有检查项 status 均为 'passed'
- **THEN** CheckSuite.status 更新为 'awaiting-approval'

#### Scenario: 任何检查失败后 suite 状态变更
- **WHEN** 任何检查项 status 为 'failed'
- **THEN** CheckSuite.status 更新为 'failed'

### Requirement: Check stage 循环重跑

Check stage SHALL 以循环方式执行检查，最多重试 3 次。每次循环按顺序执行 build-test → ai-review。任何检查失败且 auto-fix 成功时，从 build-test 重新开始。

#### Scenario: 首次循环全部通过
- **WHEN** Check stage 开始第 1 次循环
- **AND** build-test 通过
- **AND** ai-review 通过
- **THEN** CheckSuite.status 变为 'awaiting-approval'
- **AND** StageRunResult.requiresApproval = true

#### Scenario: build-test 失败后 auto-fix 成功重跑
- **WHEN** 第 N 次循环中 build-test 失败
- **AND** auto-fix 成功产生新 commit
- **AND** N < maxRetries (3)
- **THEN** snapshotSha 更新，所有检查项重置
- **AND** 开始第 N+1 次循环

#### Scenario: ai-review 失败后 auto-fix 成功重跑
- **WHEN** 第 N 次循环中 build-test 通过
- **AND** ai-review 失败
- **AND** auto-fix 成功产生新 commit
- **AND** N < maxRetries (3)
- **THEN** snapshotSha 更新，所有检查项重置
- **AND** 开始第 N+1 次循环（从 build-test 重新开始）

#### Scenario: 达到最大重试次数
- **WHEN** 第 3 次循环仍有检查失败
- **THEN** CheckSuite.status 变为 'failed'
- **AND** StageRunResult.success = false
- **AND** 不再尝试 auto-fix

#### Scenario: auto-fix 未产生新 commit
- **WHEN** 检查失败后 auto-fix 执行
- **AND** auto-fix 未产生新 commit（代码无变化）
- **THEN** 视为重试失败
- **AND** 继续下一次循环或达到 maxRetries

### Requirement: 移除 MergeReadyCheck

系统 SHALL 移除 MergeReadyCheck 检查项。merge-ready 不是代码质量检查，是合并状态检查，应在合并流程中处理。

#### Scenario: CheckSuite.checks 不包含 merge-ready
- **WHEN** CheckSuite 创建
- **THEN** checks 中只包含 `build-test` 和 `ai-review`
- **AND** 不包含 `merge-ready`

#### Scenario: MergeReadyCheck 类被删除
- **WHEN** 检查 `packages/cli/src/workflow/checks/merge-ready-check.ts`
- **THEN** 该文件不存在

#### Scenario: CheckStageRunner 构造不包含 MergeReadyCheck
- **WHEN** AgentRunnerService 构建 CheckStageRunner
- **THEN** checks 数组中不包含 MergeReadyCheck 实例

### Requirement: Check Suite API 查询

系统 SHALL 提供 `GET /api/issues/:number/check-suite` 端点，返回该 issue 当前活跃的 CheckSuite。

#### Scenario: 查询活跃 CheckSuite
- **WHEN** 请求 `GET /api/issues/1/check-suite`
- **AND** issue #1 有活跃 CheckSuite
- **THEN** 返回 200 和 CheckSuite JSON

#### Scenario: 无活跃 CheckSuite
- **WHEN** 请求 `GET /api/issues/1/check-suite`
- **AND** issue #1 无活跃 CheckSuite
- **THEN** 返回 200 和 `null`

### Requirement: Check Suite 事件通知

系统 SHALL 在检查项状态变更时通过 EventBus 发送事件，供前端实时更新。复用现有 `check_update` 事件（扩展 `snapshotSha` 字段），新增 `check_suite_status_changed` 事件用于 suite 级别状态变更。

#### Scenario: 检查项状态变更事件
- **WHEN** 检查项 status 从 pending 变为 running
- **THEN** EventBus emit `check_update` 事件
- **AND** payload 包含 `{ issueId, projectId, issueNumber, checkName, status, snapshotSha }`

#### Scenario: CheckSuite 状态变更事件
- **WHEN** CheckSuite status 变更（如 running → awaiting-approval）
- **THEN** EventBus emit `check_suite_status_changed` 事件
- **AND** payload 包含 `{ issueId, projectId, issueNumber, suiteStatus, snapshotSha }`
