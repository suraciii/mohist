## ADDED Requirements

### Requirement: Session 状态模型简化

ExploreStatus 枚举 SHALL 保留 `crystallized` 值以兼容 ACP 路由，但非 ACP 代码路径 SHALL 不再使用 `crystallized` 状态。新创建的 session 状态 SHALL 为 `active`，`crystallized` 状态 SHALL 仅由 ACP crystallize 路由设置。前端 SHALL 将 `crystallized` 视为 `active` 显示。

#### Scenario: 已有 crystallized session 兼容处理
- **WHEN** 数据库中存在 `status = 'crystallized'` 的 session
- **THEN** 这些 session SHALL 被视为 `active` 状态处理
- **AND** 不因历史数据导致错误

#### Scenario: 新 session 创建为 active
- **WHEN** 创建新 explore session
- **THEN** session.status 为 `active`

### Requirement: create_issue tool 不再 crystallize session

`create_issue` tool 执行后 SHALL 仅更新 session 的 issueId，不改变 session 状态。session 保持 `active`，用户可继续对话。

#### Scenario: create_issue 后 session 保持 active
- **WHEN** agent 在 explore session 中调用 create_issue tool
- **THEN** issue 被创建（stage 为 Draft）
- **AND** session.issueId 被设置为新建 issue 的 ID
- **AND** session.status 保持 `active`
- **AND** `explore_crystallized` 事件不再 emit

### Requirement: 创建 session 时可选指定 issueId

`ExploreSessionRepo.create()` SHALL 接受可选 `issueId` 参数。POST /explore API SHALL 接受可选 `issueId` 字段。

#### Scenario: 创建 session 并关联 issue
- **WHEN** 请求 `POST /api/explore` with `{ projectId, issueId }`
- **AND** issueId 对应的 issue 存在
- **THEN** 创建 session 并设置 `issue_id` 为传入值
- **AND** 返回 session 信息，其中 issueId 为传入值

#### Scenario: 创建 session 不指定 issueId
- **WHEN** 请求 `POST /api/explore` with `{ projectId }`
- **THEN** 创建 session，`issue_id` 为 null
- **AND** session 可后续通过 create_issue 或 updateIssueId 关联 issue

#### Scenario: 同一 issue 只关联一个 session
- **WHEN** 请求 `POST /api/explore` with `{ projectId, issueId }`
- **AND** 已存在一个 session 关联了该 issueId
- **THEN** 返回 409 Conflict
- **AND** 错误信息提示该 issue 已有关联 session

### Requirement: ExploreSession 类型包含 issueNumber

`ExploreSession` 类型 SHALL 新增可选 `issueNumber` 字段。GET /explore 列表 API SHALL join issues 表返回 issueNumber。

#### Scenario: 列表 API 返回 issueNumber
- **WHEN** 请求 `GET /api/explore?projectId=x`
- **AND** 某个 session 的 issueId 不为 null
- **THEN** 该 session 的响应包含 `issueNumber`（从 issues 表 join 获取）

#### Scenario: session 无关联 issue 时 issueNumber 为 undefined
- **WHEN** 请求 `GET /api/explore?projectId=x`
- **AND** 某个 session 的 issueId 为 null
- **THEN** 该 session 的响应中 `issueNumber` 为 `undefined` 或不包含该字段

### Requirement: Agent prompt 感知关联 issue

`runExploreAgent` SHALL 根据 session 的 issueId 和对应 issue 的 stage 动态注入 system prompt 附加说明。

#### Scenario: session 关联 Draft issue
- **WHEN** session.issueId 存在
- **AND** 对应 issue.stage 为 `draft`
- **THEN** system prompt 附加说明：可用 update_issue tool 更新该 issue 的 title/body/labels
- **AND** 可用 create_issue 创建新 issue（如需）

#### Scenario: session 关联非 Draft issue
- **WHEN** session.issueId 存在
- **AND** 对应 issue.stage 不是 `draft`
- **THEN** system prompt 附加说明：关联 issue 已启动实施，不可再通过 update_issue 修改

#### Scenario: session 无关联 issue
- **WHEN** session.issueId 为 null
- **THEN** system prompt 附加说明：可用 create_issue 创建新 issue

### Requirement: update_issue tool

系统 SHALL 提供 `update_issue` tool，仅在 session 有 issueId 且对应 issue 为 Draft 时可用。tool 能更新 issue 的 title、body 和 labels。

#### Scenario: 更新 Draft issue
- **WHEN** session.issueId 存在
- **AND** 对应 issue.stage 为 `draft`
- **AND** agent 调用 update_issue tool with `{ title?, body?, labels? }`
- **THEN** issue 的对应字段被更新
- **AND** 返回更新成功的确认信息

#### Scenario: issue 非 Draft 时拒绝更新
- **WHEN** session.issueId 存在
- **AND** 对应 issue.stage 不是 `draft`
- **AND** agent 调用 update_issue tool
- **THEN** tool 返回错误信息 "Issue is no longer in Draft stage, cannot update"

#### Scenario: session 无关联 issue 时 tool 不可用
- **WHEN** session.issueId 为 null
- **THEN** update_issue tool 不注册到 tool registry
- **AND** agent 无法调用该 tool
