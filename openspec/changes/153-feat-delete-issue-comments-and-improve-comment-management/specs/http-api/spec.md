## MODIFIED Requirements

### Requirement: API 提供操作接口

Server SHALL provide RESTful APIs for issue operations, including adding and deleting issue comments. API handlers SHALL use IssueService for issue data operations and SHALL NOT bypass service-level ownership validation.

#### Scenario: 添加评论
- **WHEN** CLI 请求 `POST /api/issues/:number/comments` with `{ body }`
- **THEN** 通过 IssueService 创建 comment
- **AND** 返回 comment 信息

#### Scenario: 删除评论
- **WHEN** 客户端请求 `DELETE /api/issues/:number/comments/:commentId`
- **AND** 当前项目存在
- **AND** issue 存在
- **AND** comment 存在且属于该 issue
- **THEN** 通过 IssueService 删除 comment
- **AND** 返回成功结果

#### Scenario: 删除不存在或不属于该 Issue 的评论
- **WHEN** 客户端请求 `DELETE /api/issues/:number/comments/:commentId`
- **AND** comment 不存在或不属于该 issue
- **THEN** Server 返回 404
- **AND** 响应包含可理解的错误信息

#### Scenario: 删除评论不影响其他数据
- **WHEN** 删除一个属于指定 issue 的 comment 成功
- **THEN** 指定 issue 仍然存在
- **AND** 其他 issue 的 comments 不被删除

### Requirement: API 处理错误情况

Server SHALL return clear error responses for issue comment deletion failures.

#### Scenario: 删除评论时 Issue 不存在
- **WHEN** 请求 `DELETE /api/issues/:number/comments/:commentId`
- **AND** Issue number 不存在于当前项目
- **THEN** 返回 404 错误
- **AND** 包含错误信息 "Issue not found" 或等价信息

#### Scenario: 无当前项目时删除评论
- **WHEN** 请求 `DELETE /api/issues/:number/comments/:commentId`
- **AND** server 无当前 project 上下文
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "No active project" 或等价信息
