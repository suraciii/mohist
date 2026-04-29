## ADDED Requirements

### Requirement: Commits API 返回完整的 commit 列表及正确的统计信息

`GET /api/issues/:number/commits` SHALL 返回指定 issue 分支上所有 commit 的完整列表，每条 commit 包含正确的 `filesChanged`、`additions`、`deletions` 统计。git log 解析 SHALL 使用不会与 `--stat` 输出冲突的分隔符。

#### Scenario: 多条 commit 全部返回且统计正确
- **WHEN** issue 分支上有 N 条 commit（N > 1）
- **THEN** `GET /api/issues/:number/commits` 返回 N 条 commit
- **AND** 每条 commit 的 `filesChanged`、`additions`、`deletions` 反映实际 git diff 统计

#### Scenario: 单条 commit 正确返回
- **WHEN** issue 分支上只有 1 条 commit
- **THEN** 返回 1 条 commit，统计信息正确

#### Scenario: 无 worktree 时返回空数组
- **WHEN** issue 没有对应的 worktree
- **THEN** `GET /api/issues/:number/commits` 返回 `{ success: true, data: { commits: [] } }`

#### Scenario: 不再出现零统计的 commit
- **WHEN** issue 分支上有实际文件改动的 commit
- **THEN** 返回的 commit 中不出现 `filesChanged=0, additions=0, deletions=0`（除非该 commit 确实无文件改动）

#### Scenario: commit header 字段完整
- **WHEN** 返回任意一条 commit
- **THEN** 该 commit 包含有效的 `hash`（非空短哈希）、`message`（非空）、`author`（非空）、`date`（ISO 8601 格式）
