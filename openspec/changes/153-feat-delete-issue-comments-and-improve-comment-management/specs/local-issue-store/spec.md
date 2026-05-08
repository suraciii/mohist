## MODIFIED Requirements

### Requirement: CLI 可以为 Issue 添加评论

CLI SHALL support append-only comment creation and single-comment deletion for local issues.

#### Scenario: 添加评论
- **WHEN** 用户执行 `mo issue comment <id> "comment text"`
- **THEN** Server 创建 comment 记录
- **AND** CLI 显示 "Comment added to <project#number>"

#### Scenario: 删除 Issue 评论
- **WHEN** 用户删除指定 issue 下的指定 comment
- **THEN** Server 删除该 comment 记录
- **AND** 后续 issue detail 不再返回该 comment
- **AND** issue 本体仍然存在

#### Scenario: 拒绝跨 Issue 删除评论
- **WHEN** 用户尝试从 issue A 删除属于 issue B 的 comment
- **THEN** Server SHALL NOT 删除该 comment
- **AND** issue A 和 issue B 的其他 comments 不受影响

#### Scenario: 删除不存在的评论
- **WHEN** 用户尝试删除不存在的 comment
- **THEN** Server SHALL 返回可理解的 not found 错误

### Requirement: 查看 Issue 详情

CLI SHALL display Issue details and comments in a form that lets users identify individual comments.

#### Scenario: 查看 Issue 详情
- **WHEN** 用户执行 `mo issue show <id>`
- **AND** `<id>` 是 number 或 `project#number` 格式
- **THEN** CLI 显示 Issue 详情（title, body, stage, status, labels）和所有 comments
- **AND** 每条 comment 显示可用于删除该 comment 的 id 或短 id
