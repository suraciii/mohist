## MODIFIED Requirements

### Requirement: API 返回 issue diff 包含精确统计和完整 patch

Server SHALL 提供 `GET /api/issues/:number/diff` 端点，返回 worktree branch 相对 base branch 的文件变更。响应 SHALL 包含每个文件的精确 additions/deletions 计数（来自 `git diff --numstat`）和完整 unified diff 内容（来自 `git diff`），以及总汇总统计。

#### Scenario: 有 worktree 且有文件变更

- **WHEN** 请求 `GET /api/issues/1/diff`
- **AND** issue #1 存在且有 worktree
- **AND** branch `mo/issue-1` 相对 base branch 有文件变更
- **THEN** 返回 `{ success: true, data: { files: [...], totalAdditions: N, totalDeletions: M } }`
- **AND** 每个 file 条目包含 `file`（路径）、`additions`（精确行数，来自 numstat）、`deletions`（精确行数，来自 numstat）、`diff`（该文件的完整 unified diff 字符串）
- **AND** `totalAdditions` 和 `totalDeletions` 为所有文件的精确汇总

#### Scenario: 无 worktree

- **WHEN** 请求 `GET /api/issues/1/diff`
- **AND** issue #1 无 worktree
- **THEN** 返回 `{ success: true, data: { files: [], totalAdditions: 0, totalDeletions: 0 } }`

#### Scenario: 二进制文件

- **WHEN** 变更中包含二进制文件
- **THEN** 该文件条目的 `additions` 和 `deletions` 为 0
- **AND** `diff` 字段为 `"Binary file, no diff available"`

#### Scenario: issue 不存在

- **WHEN** 请求 `GET /api/issues/999/diff`
- **AND** issue #999 不存在
- **THEN** 返回 404 错误

#### Scenario: 无活跃项目

- **WHEN** 请求 `GET /api/issues/1/diff`
- **AND** 无当前项目上下文
- **THEN** 返回 400 错误，错误信息包含 "no active project"
