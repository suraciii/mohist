## MODIFIED Requirements

### Requirement: CHECK stage 失败后回到 PLAN

Pipeline SHALL 支持 PLAN → BUILD → CHECK 循环。当 CHECK stage 发现问题时，Issue 回到 PLAN stage 制定修复计划。

#### Scenario: CHECK 发现问题回到 PLAN
- **WHEN** CHECK stage 的审查产出问题列表
- **AND** 问题列表不为空
- **THEN** Issue stage 从 `check` 变为 `plan`
- **AND** PLAN stage 基于审查报告制定修复计划

#### Scenario: CHECK 通过完成 Issue
- **WHEN** CHECK stage 审查通过
- **THEN** Issue stage 从 `check` 变为 `done`
- **AND** Issue status 变为 `completed`

## ADDED Requirements

### Requirement: Done 阶段设置 Completed 状态

系统 SHALL 在 Issue 到达 `done` 阶段时将 status 设置为 `completed`。`IssueStatus` 枚举 SHALL 包含 `completed` 值。

#### Scenario: Pipeline 完成设置 completed
- **WHEN** workflow controller 的 run loop 结束（stage 到达 `done`）
- **THEN** Issue status SHALL 被更新为 `completed`

#### Scenario: Issue list 显示 completed
- **WHEN** 用户执行 `mo issue list`
- **AND** 有 stage 为 `done` 的 issue
- **THEN** 该 issue 的 Status 列 SHALL 显示 `completed`

#### Scenario: 已有 active 的 done issue 不受影响
- **WHEN** 系统启动时发现 stage 为 `done` 但 status 为 `active` 的 issue
- **THEN** 系统 SHALL 将其 status 更新为 `completed`

#### Scenario: resume 不影响 completed issue
- **WHEN** 用户尝试 resume 一个 `completed` 的 issue
- **THEN** 系统 SHALL 拒绝该操作
- **AND** issue 保持 `completed` 状态不变
