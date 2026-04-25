## MODIFIED Requirements

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息

## ADDED Requirements

### Requirement: Explore session 列表 API 返回 issueNumber

`GET /api/explore` 列表端点 SHALL 对每个 session 返回关联 issue 的 number 字段。

#### Scenario: 列表 session 带 issueNumber
- **WHEN** 请求 `GET /api/explore?projectId=x`
- **THEN** 每个 session 的响应中包含 `issueNumber?: number` 字段
- **AND** 通过 join issues 表（ON issue_id = issues.id）获取 issue.number

### Requirement: Explore session 创建 API 接受 issueId

`POST /api/explore` 端点 SHALL 接受可选 `issueId` 参数，用于创建时直接关联 issue。

#### Scenario: 创建 session 并关联 issue
- **WHEN** 请求 `POST /api/explore` with `{ projectId?, title?, issueId? }`
- **AND** issueId 不为空且对应 issue 存在
- **THEN** 创建 session 并设置 issue_id
- **AND** 返回 201 及 session 信息（含 issueNumber）

#### Scenario: issueId 对应的 issue 不存在
- **WHEN** 请求 `POST /api/explore` with `{ issueId: "nonexistent" }`
- **THEN** 返回 404 错误
- **AND** 错误信息包含 "Issue not found"

#### Scenario: issueId 已被其他 session 关联
- **WHEN** 请求 `POST /api/explore` with `{ issueId }`
- **AND** 另一个 session 已关联该 issueId
- **THEN** 返回 409 Conflict
- **AND** 错误信息提示该 issue 已有关联 session

### Requirement: Explore session 详情 API 返回 issueNumber

`GET /api/explore/:id` 端点 SHALL 在返回的 session 对象中包含 issueNumber 字段。

#### Scenario: 详情 API 返回 issueNumber
- **WHEN** 请求 `GET /api/explore/:id`
- **AND** session 的 issueId 不为 null
- **THEN** 返回的 session 对象包含 `issueNumber`（从 issues 表 join 获取）

#### Scenario: session 无关联 issue 时 issueNumber 为 undefined
- **WHEN** 请求 `GET /api/explore/:id`
- **AND** session 的 issueId 为 null
- **THEN** 返回的 session 对象中 `issueNumber` 为 `undefined` 或不包含该字段
