## MODIFIED Requirements

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作，基于 Hono 框架实现。API handler SHALL 通过 IssueService 操作 issue 数据，不直接调用 StateManager 的 CRUD 方法。

#### Scenario: 创建 Issue
- **WHEN** CLI 请求 `POST /api/issues` with `{ title, body?, labels? }`
- **THEN** 通过 IssueService 创建 Issue
- **AND** 返回 Issue 信息

#### Scenario: 更新 Issue
- **WHEN** CLI 请求 `PATCH /api/issues/:number` with `{ title?, body?, addLabels?, removeLabels?, priority?, model? }`
- **THEN** 通过 IssueService 更新 Issue
- **AND** 如果 `model` 为 `"provider/model-id"` 格式的字符串，设置 per-issue model override
- **AND** 如果 `model` 为 `null`，清除 per-issue model override（fallback 到 stageModels/global）
- **AND** 如果 `model` 为 `undefined`，不修改 model 字段
- **AND** 返回更新后的 Issue（包含 `model` 字段）

#### Scenario: 更新 Issue model 为无效格式
- **WHEN** CLI 请求 `PATCH /api/issues/:number` with `{ model: "invalid-no-slash" }`
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "Invalid model format"

#### Scenario: 添加评论
- **WHEN** CLI 请求 `POST /api/issues/:number/comments` with `{ body }`
- **THEN** 通过 IssueService 创建 comment
- **AND** 返回 comment 信息

#### Scenario: 启动 Issue 处理
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **THEN** Main Agent 被启动处理该 Issue
- **AND** 返回 Issue 信息和运行状态
