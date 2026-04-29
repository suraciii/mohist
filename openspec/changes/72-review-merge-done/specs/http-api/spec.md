## MODIFIED Requirements

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作，基于 Hono 框架实现。API handler SHALL 通过 IssueService 操作 issue 数据，不直接调用 StateManager 的 CRUD 方法。

#### Scenario: 创建 Issue

- **WHEN** CLI 请求 `POST /api/issues` with `{ title, body?, labels? }`
- **THEN** 通过 IssueService 创建 Issue
- **AND** 返回 Issue 信息

#### Scenario: 更新 Issue

- **WHEN** CLI 请求 `PATCH /api/issues/:number` with `{ title?, body?, addLabels?, removeLabels? }`
- **THEN** 通过 IssueService 更新 Issue
- **AND** 返回更新后的 Issue

#### Scenario: 添加评论

- **WHEN** CLI 请求 `POST /api/issues/:number/comments` with `{ body }`
- **THEN** 通过 IssueService 创建 comment
- **AND** 返回 comment 信息

#### Scenario: 启动 Issue 处理

- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **THEN** Main Agent 被启动处理该 Issue
- **AND** 返回 Issue 信息和运行状态

#### Scenario: 审批 Plan 阶段

- **WHEN** CLI 请求 `POST /api/issues/:number/approve`
- **AND** issue 的 `approvalState.stage === Plan`
- **THEN** issue stage 变为 `build`
- **AND** pipeline 从 Build 阶段继续执行

#### Scenario: 审批 Review 阶段

- **WHEN** CLI 请求 `POST /api/issues/:number/approve`
- **AND** issue 的 `approvalState.stage === Review`
- **THEN** issue 的 `approvalState.status` 变为 `approved`
- **AND** issue 保持在 `review` 阶段（SHALL NOT 设 `nextStage = Done`）
- **AND** pipeline resume 后 WorkflowController 在 Review 阶段识别已审批状态，执行 mergeBack
- **AND** mergeBack 成功后才进入 `done`
