## ADDED Requirements

### Requirement: CLI 提供 issue archive 命令

CLI SHALL 提供 `mo issue archive` 子命令用于归档 issue。

#### Scenario: 归档单个 issue
- **WHEN** 用户执行 `mo issue archive <number>`
- **THEN** CLI 调用 `POST /api/issues/:number/archive`
- **AND** CLI 显示归档结果 "Issue #{N} archived." 及资源清理摘要

#### Scenario: 归档不清理资源
- **WHEN** 用户执行 `mo issue archive <number> --no-cleanup`
- **THEN** CLI 调用 `POST /api/issues/:number/archive` with `{ cleanup: false }`
- **AND** CLI 显示 "Issue #{N} archived (resources preserved)."

#### Scenario: 批量归档已完成 issue
- **WHEN** 用户执行 `mo issue archive --all-completed`
- **THEN** CLI 调用 `POST /api/issues/archive-completed`
- **AND** CLI 显示归档数量 "Archived {N} issues."

#### Scenario: 批量归档无可归档 issue
- **WHEN** 用户执行 `mo issue archive --all-completed`
- **AND** server 返回 archived=0
- **THEN** CLI 显示 "No completed issues to archive."

#### Scenario: 归档不存在的 issue
- **WHEN** 用户执行 `mo issue archive <number>`
- **AND** issue 不存在
- **THEN** CLI 显示错误 "Issue #{N} not found."

### Requirement: CLI 提供 issue unarchive 命令

CLI SHALL 提供 `mo issue unarchive` 子命令用于恢复已归档的 issue。

#### Scenario: 恢复归档 issue
- **WHEN** 用户执行 `mo issue unarchive <number>`
- **THEN** CLI 调用 `POST /api/issues/:number/unarchive`
- **AND** CLI 显示 "Issue #{N} unarchived."

#### Scenario: 恢复未归档的 issue
- **WHEN** 用户执行 `mo issue unarchive <number>`
- **AND** issue 未被归档
- **THEN** CLI 显示错误 "Issue #{N} is not archived."

## MODIFIED Requirements

### Requirement: CLI 支持本地 Issue CRUD

CLI SHALL 通过 Server API 支持本地 Issue 的创建、读取、更新、删除操作。

#### Scenario: CLI 调用 Server API 创建 Issue
- **WHEN** 用户执行 `mo issue create "title"`
- **THEN** CLI 发送 POST /api/issues 请求到 Server
- **AND** Server 在本地 SQLite 创建 Issue
- **AND** CLI 显示创建结果

#### Scenario: CLI 调用 Server API 列出 Issues
- **WHEN** 用户执行 `mo issue list`
- **THEN** CLI 发送 GET /api/issues 请求到 Server（不含 archived 参数）
- **AND** Server 返回未归档的 issue
- **AND** CLI 格式化显示结果

#### Scenario: CLI 列出已归档 Issues
- **WHEN** 用户执行 `mo issue list --archived`
- **THEN** CLI 发送 GET /api/issues?archived=true 请求到 Server
- **AND** CLI 格式化显示已归档 issue，包含归档时间

#### Scenario: CLI 列出所有 Issues
- **WHEN** 用户执行 `mo issue list --all`
- **THEN** CLI 发送 GET /api/issues?all=true 请求到 Server
- **AND** CLI 格式化显示所有 issue，已归档的标注 `(archived)`

#### Scenario: CLI 调用 Server API 更新 Issue
- **WHEN** 用户执行 `mo issue update <id> --title "new"`
- **THEN** CLI 发送 PATCH /api/issues/:id 请求到 Server
- **AND** Server 更新本地 SQLite
- **AND** CLI 显示更新结果

#### Scenario: CLI 调用 Server API 添加评论
- **WHEN** 用户执行 `mo issue comment <id> "text"`
- **THEN** CLI 发送 POST /api/issues/:id/comments 请求到 Server
- **AND** Server 在本地 SQLite 创建 comment
- **AND** CLI 显示成功消息
