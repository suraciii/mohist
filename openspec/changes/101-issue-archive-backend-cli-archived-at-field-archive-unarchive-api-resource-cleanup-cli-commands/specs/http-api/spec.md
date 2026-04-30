## MODIFIED Requirements

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态
- **AND** 默认排除已归档 issue 的计数

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态
- **AND** 默认排除已归档 issue 的计数

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息
- **AND** 如果 issue 已归档，响应中包含 `archived_at` 字段

#### Scenario: 列出活跃 Issues
- **WHEN** CLI 请求 `GET /api/issues`
- **THEN** 返回当前项目中 `archived_at IS NULL` 的 issue 列表
- **AND** 按 `updated_at` 降序排列

#### Scenario: 列出已归档 Issues
- **WHEN** CLI 请求 `GET /api/issues?archived=true`
- **THEN** 返回当前项目中 `archived_at IS NOT NULL` 的 issue 列表
- **AND** 按 `archived_at` 降序排列

#### Scenario: 列出所有 Issues（含归档）
- **WHEN** CLI 请求 `GET /api/issues?all=true`
- **THEN** 返回当前项目中所有 issue（包含已归档的）
- **AND** 兼容旧行为

## ADDED Requirements

### Requirement: API 提供归档操作端点

Server SHALL 提供归档相关的 RESTful API 端点。

#### Scenario: 归档单个 Issue
- **WHEN** CLI 请求 `POST /api/issues/:number/archive`
- **THEN** 系统标记 issue 为已归档（设置 archived_at）
- **AND** 默认执行资源清理（worktree 移除 + openspec 归档）
- **AND** 返回 200 及归档结果摘要

#### Scenario: 归档单个 Issue 不清理资源
- **WHEN** CLI 请求 `POST /api/issues/:number/archive` with `{ cleanup: false }`
- **THEN** 系统仅标记 archived_at
- **AND** 不执行 worktree 移除或 openspec 归档

#### Scenario: 归档运行中的 Issue 被拒绝
- **WHEN** CLI 请求 `POST /api/issues/:number/archive`
- **AND** issue 有活跃 agent session
- **THEN** 返回 409 Conflict
- **AND** 错误信息包含 "running agent"

#### Scenario: 归档不存在的 Issue
- **WHEN** CLI 请求 `POST /api/issues/:number/archive`
- **AND** issue 不存在
- **THEN** 返回 404

#### Scenario: 取消归档 Issue
- **WHEN** CLI 请求 `POST /api/issues/:number/unarchive`
- **THEN** 系统清除 archived_at
- **AND** 尝试恢复 openspec 目录
- **AND** 返回 200

#### Scenario: 取消归档未归档的 Issue
- **WHEN** CLI 请求 `POST /api/issues/:number/unarchive`
- **AND** issue 的 archived_at 为 NULL
- **THEN** 返回 400
- **AND** 错误信息包含 "not archived"

#### Scenario: 批量归档已完成 Issue
- **WHEN** CLI 请求 `POST /api/issues/archive-completed`
- **THEN** 系统归档当前项目中所有 stage=done 且 archived_at IS NULL 的 issue
- **AND** 对每个 issue 执行资源清理
- **AND** 返回 200 及归档数量 `{ archived: N }`

#### Scenario: 批量归档无可归档 Issue
- **WHEN** CLI 请求 `POST /api/issues/archive-completed`
- **AND** 没有符合条件的 issue
- **THEN** 返回 200 及 `{ archived: 0, message: "No completed issues to archive." }`
