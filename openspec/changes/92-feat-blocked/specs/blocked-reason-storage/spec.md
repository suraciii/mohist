## ADDED Requirements

### Requirement: blockedReason 字段持久化

系统 SHALL 在 issues 表中增加 `blocked_reason TEXT` 字段，用于存储人话格式的 blocked 原因。该字段在 issue 离开 blocked 状态时 SHALL 被清除（设为 null）。

#### Scenario: Issue 被标记 blocked 时写入 reason

- **WHEN** 系统将 issue status 设为 blocked
- **THEN** blocked_reason 字段被写入人话格式的 reason 字符串
- **AND** reason 不包含技术日志格式（如 `status=blocked, 2/8 tasks completed`），而是自然语言描述

#### Scenario: Issue 离开 blocked 状态时清除 reason

- **WHEN** issue 通过 reopen、retry 或 restart 离开 blocked 状态
- **THEN** blocked_reason 被设为 null

#### Scenario: blockedReason 在 DB schema 中持久化

- **WHEN** 数据库执行 schema migration
- **THEN** issues 表增加 `blocked_reason` 列（TEXT, 默认 null）

### Requirement: blockedReason 使用人话格式

所有写入 blockedReason 的内容 SHALL 使用用户可理解的自然语言描述，包含：发生了什么、当前进度、建议操作。

#### Scenario: Build 阶段部分完成的 reason

- **WHEN** build 阶段因 agent 异常中断，部分 task 已完成
- **THEN** blockedReason 格式为 "Build 中断 — 完成了 {n}/{total} 个任务后 agent 进程异常退出。可从断点恢复。"

#### Scenario: 重试耗尽的 reason

- **WHEN** 自动重试 3 次后仍然失败
- **THEN** blockedReason 格式为 "Agent 在 {stage} 阶段反复失败（已自动重试 3 次），需要人工介入。点击"重试"从断点继续，或"重新开始"丢弃进度。"

#### Scenario: 资源缺失的 reason

- **WHEN** project 或 worktree 不存在
- **THEN** blockedReason 格式为 "无法恢复 — {资源} 不存在。可能已被手动删除。"

#### Scenario: Merge conflict 的 reason

- **WHEN** rebase 因 merge conflict 失败
- **THEN** blockedReason 包含冲突文件列表和重试次数

### Requirement: blockedReason 通过 API 暴露

API 返回 issue 数据时 SHALL 包含 blockedReason 字段。

#### Scenario: GET /api/issues/:number 返回 blockedReason

- **WHEN** 请求 `GET /api/issues/:number`
- **AND** issue 的 status 为 blocked
- **THEN** 响应中的 issue 对象包含 `blockedReason` 字段（非 null 字符串）

#### Scenario: 非 blocked issue 的 blockedReason

- **WHEN** 请求 `GET /api/issues/:number`
- **AND** issue 的 status 不为 blocked
- **THEN** 响应中的 issue 对象的 `blockedReason` 为 null
