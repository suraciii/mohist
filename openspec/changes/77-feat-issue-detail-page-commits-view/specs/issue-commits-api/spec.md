## ADDED Requirements

### Requirement: API 返回 issue commits 列表

Server SHALL 提供 `GET /api/issues/:number/commits` 端点，返回 worktree branch (`mo/issue-{N}`) 相对于 base branch 的提交历史。每个 commit 条目 SHALL 包含 `hash`（短 hash，7 字符）、`message`（首行）、`author`、`date`（ISO 8601）、`filesChanged`、`additions`、`deletions`。

#### Scenario: 有 worktree 且有 commits

- **WHEN** 请求 `GET /api/issues/1/commits`
- **AND** issue #1 存在且有 worktree
- **AND** branch `mo/issue-1` 相对 base branch 有 3 个 commit
- **THEN** 返回 `{ success: true, data: { commits: [...] } }` 包含 3 个条目
- **AND** 每个 entry 包含 `hash`、`message`、`author`、`date`、`filesChanged`、`additions`、`deletions`
- **AND** commits 按时间倒序排列（最新在前）

#### Scenario: 无 worktree

- **WHEN** 请求 `GET /api/issues/1/commits`
- **AND** issue #1 无 worktree
- **THEN** 返回 `{ success: true, data: { commits: [] } }`

#### Scenario: worktree 存在但无 commit

- **WHEN** 请求 `GET /api/issues/1/commits`
- **AND** worktree 存在但 branch 与 base branch 无差异
- **THEN** 返回 `{ success: true, data: { commits: [] } }`

#### Scenario: issue 不存在

- **WHEN** 请求 `GET /api/issues/999/commits`
- **AND** issue #999 不存在
- **THEN** 返回 404 错误

#### Scenario: 无活跃项目

- **WHEN** 请求 `GET /api/issues/1/commits`
- **AND** 无当前项目上下文
- **THEN** 返回 400 错误，错误信息包含 "no active project"

### Requirement: API 返回单个 commit diff

Server SHALL 提供 `GET /api/issues/:number/commits/:hash/diff` 端点，返回指定 commit 的 diff 内容。

#### Scenario: 获取有效 commit 的 diff

- **WHEN** 请求 `GET /api/issues/1/commits/a1b2c3d/diff`
- **AND** commit hash 属于 `mo/issue-1` branch
- **THEN** 返回 `{ success: true, data: { hash: "a1b2c3d", diff: "..." } }`
- **AND** `diff` 为 `git show <hash> --format="" --patch` 的原始输出

#### Scenario: commit 不属于该 issue branch

- **WHEN** 请求 `GET /api/issues/1/commits/xyz1234/diff`
- **AND** commit xyz1234 不属于 `mo/issue-1` branch
- **THEN** 返回 404 错误

#### Scenario: 无 worktree

- **WHEN** 请求 `GET /api/issues/1/commits/a1b2c3d/diff`
- **AND** issue #1 无 worktree
- **THEN** 返回 404 错误

#### Scenario: issue 不存在

- **WHEN** 请求 `GET /api/issues/999/commits/a1b2c3d/diff`
- **AND** issue #999 不存在
- **THEN** 返回 404 错误

#### Scenario: 无活跃项目

- **WHEN** 请求 `GET /api/issues/1/commits/a1b2c3d/diff`
- **AND** 无当前项目上下文
- **THEN** 返回 400 错误，错误信息包含 "no active project"
