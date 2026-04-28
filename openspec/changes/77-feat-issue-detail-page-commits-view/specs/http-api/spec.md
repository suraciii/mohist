## ADDED Requirements

### Requirement: API 提供 issue commits 列表端点

Server SHALL 提供 `GET /api/issues/:number/commits` 端点，返回 issue worktree branch 相对 base branch 的 commit 列表。端点 SHALL 验证项目上下文、issue 存在性和 worktree 存在性，遵循与 `GET /:number/diff` 一致的错误处理模式。

#### Scenario: 列出 commits

- **WHEN** 请求 `GET /api/issues/:number/commits`
- **AND** issue 存在且有 worktree
- **THEN** 返回 `{ success: true, data: { commits: [...] } }`

#### Scenario: 无 project 上下文时返回 400

- **WHEN** 请求 `GET /api/issues/:number/commits`
- **AND** 无当前 project
- **THEN** 返回 400 错误

#### Scenario: Issue 不存在时返回 404

- **WHEN** 请求 `GET /api/issues/:number/commits`
- **AND** issue 不存在
- **THEN** 返回 404 错误

### Requirement: API 提供单 commit diff 端点

Server SHALL 提供 `GET /api/issues/:number/commits/:hash/diff` 端点，返回指定 commit 的 patch diff。端点 SHALL 验证 commit hash 属于该 issue 的 worktree branch。

#### Scenario: 获取 commit diff

- **WHEN** 请求 `GET /api/issues/:number/commits/:hash/diff`
- **AND** commit 属于该 issue branch
- **THEN** 返回 `{ success: true, data: { hash, diff } }`

#### Scenario: commit 不属于 issue branch 时返回 404

- **WHEN** 请求 `GET /api/issues/:number/commits/:hash/diff`
- **AND** commit 不属于 `mo/issue-{N}` branch
- **THEN** 返回 404 错误
