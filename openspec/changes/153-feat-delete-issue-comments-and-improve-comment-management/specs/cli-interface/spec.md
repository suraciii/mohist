## MODIFIED Requirements

### Requirement: CLI 支持本地 Issue CRUD

CLI SHALL support local Issue operations through the Server API, including adding and deleting comments.

#### Scenario: CLI 调用 Server API 添加评论
- **WHEN** 用户执行 `mo issue comment <id> "text"`
- **THEN** CLI 发送 POST /api/issues/:id/comments 请求到 Server
- **AND** Server 在本地 SQLite 创建 comment
- **AND** CLI 显示成功消息

#### Scenario: CLI 显示可定位的评论 id
- **WHEN** 用户执行 `mo issue show <id>`
- **AND** issue 有 comments
- **THEN** 每条 comment 显示可用于删除的 id 或短 id

#### Scenario: CLI 调用 Server API 删除评论
- **WHEN** 用户执行 `mo issue delete-comment <id> <comment-id>`
- **THEN** CLI 发送 DELETE /api/issues/:id/comments/:commentId 请求到 Server
- **AND** CLI 输出 `Deleted comment <id> from issue #<number>`

#### Scenario: CLI 删除评论失败
- **WHEN** 用户执行 `mo issue delete-comment <id> <comment-id>`
- **AND** Server 返回错误
- **THEN** CLI 显示清晰错误信息
- **AND** 错误信息说明失败对象是 comment 而不是 issue 本体
