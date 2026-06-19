## MODIFIED Requirements

### Requirement: Server API 扩展

Server SHALL 新增以下 API 端点支持本地 Issue CRUD。Issue labels SHALL be key-value maps governed by the `issue-labels` capability: create/update request bodies accept a `labels` JSON object whose keys map to at most one value, and `GET /api/labels` returns the distinct label keys used in the project.

#### Scenario: POST /api/issues
- **WHEN** Server 收到 POST /api/issues 请求
- **WITH** body: `{ title, body?, labels?: { "key": "value", ... } }`
- **THEN** Server 在当前项目创建 Issue
- **AND** 返回 Issue 详情（labels 为 key-value map）

#### Scenario: PATCH /api/issues/:id
- **WHEN** Server 收到 PATCH /api/issues/:id 请求
- **WITH** body: `{ title?, body?, labels?: { "key": "value", ... } }`
- **THEN** Server 更新指定 Issue（labels 为全量替换的 key-value map）
- **AND** 返回更新后的 Issue

#### Scenario: POST /api/issues/:id/comments
- **WHEN** Server 收到 POST /api/issues/:id/comments 请求
- **WITH** body: `{ body }`
- **THEN** Server 创建 comment
- **AND** 返回 comment 详情

#### Scenario: GET /api/labels
- **WHEN** Server 收到 GET /api/labels 请求
- **THEN** Server 返回当前项目所有使用过的 label keys（去重）
- **AND** 按 key 名称排序
