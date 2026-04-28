## ADDED Requirements

### Requirement: API 提供 blocked merge 管理端点

API SHALL 提供端点用于查看 merge blocked 的 issue 及其冲突详情，并支持手动触发重试。

#### Scenario: 列出 blocked issues

- **WHEN** CLI 请求 `GET /api/issues/merge-blocked`
- **THEN** 返回所有 `mergeState = 'blocked'` 的 issue 列表
- **AND** 每个 issue 包含 `{ issueNumber, title, conflictingFiles, blockedAt }`
- **AND** 返回 200

#### Scenario: 无 blocked issues

- **WHEN** CLI 请求 `GET /api/issues/merge-blocked`
- **AND** 没有 `mergeState = 'blocked'` 的 issue
- **THEN** 返回空数组 `[]`

#### Scenario: 手动重试 blocked merge

- **WHEN** CLI 请求 `POST /api/issues/:number/retry-merge`
- **AND** issue 的 `mergeState` 为 `blocked`
- **THEN** issue 的 `mergeState` 重置为 `pending`
- **AND** issue 重新进入 merge queue 走 rebase-first 流程
- **AND** 返回 200

#### Scenario: 重试非 blocked issue

- **WHEN** CLI 请求 `POST /api/issues/:number/retry-merge`
- **AND** issue 的 `mergeState` 不是 `blocked` 或 `conflict` 或 `build-failed`
- **THEN** 返回 409 Conflict
- **AND** 错误信息包含当前 mergeState

#### Scenario: 重试不存在的 issue

- **WHEN** CLI 请求 `POST /api/issues/:number/retry-merge`
- **AND** issue 不存在
- **THEN** 返回 404
