## ADDED Requirements

### Requirement: Session 创建时从用户首条消息自动命名

当 Explore session 收到用户第一条消息时，系统 SHALL 自动从该消息内容截取前 50 个字符作为 session 标题，替换默认的 "New Exploration"。

#### Scenario: 首条消息触发自动命名

- **WHEN** session 标题为默认值 "New Exploration"
- **AND** 用户发送第一条消息 "How should we handle error retry logic in the pipeline?"
- **THEN** 系统 SHALL 在处理该消息时将 session 标题更新为 "How should we handle error retry logic in..."
- **AND** 标题截取时 SHALL 在单词边界处断开（避免截断单词），若截断则加 "..." 后缀，总长度不超过 50 字符

#### Scenario: 首条消息短于 50 字符

- **WHEN** session 标题为默认值 "New Exploration"
- **AND** 用户发送第一条消息 "Fix the bug"
- **THEN** 标题 SHALL 更新为 "Fix the bug"

#### Scenario: 标题已被手动修改时不覆盖

- **WHEN** session 标题已被用户手动编辑为 "API Design Discussion"
- **AND** 用户发送新消息
- **THEN** 标题 SHALL 保持 "API Design Discussion" 不变

### Requirement: PATCH 端点支持更新 session 标题

系统 SHALL 提供 `PATCH /api/explore/:id` 端点，允许前端更新 session 的标题字段。

#### Scenario: 成功更新标题

- **WHEN** 前端发送 `PATCH /api/explore/:id`，body 为 `{ "title": "New Title" }`
- **THEN** 系统 SHALL 更新该 session 的 `title` 字段
- **AND** 返回 200 状态码和更新后的 session 对象
- **AND** `updated_at` 字段 SHALL 刷新为当前时间

#### Scenario: Session 不存在

- **WHEN** 前端发送 `PATCH /api/explore/:id`，但该 ID 不存在
- **THEN** 系统 SHALL 返回 404 和 `{ success: false, error: "Session not found" }`

#### Scenario: 标题为空

- **WHEN** 前端发送 `PATCH /api/explore/:id`，body 为 `{ "title": "" }`
- **THEN** 系统 SHALL 返回 400 错误，标题不可为空

### Requirement: ExploreSessionList 卡片支持双击编辑标题

Explore session 列表中的每个 session 卡片 SHALL 支持用户双击标题进行内联编辑。

#### Scenario: 双击进入编辑模式

- **WHEN** 用户双击 session 卡片的标题文本
- **THEN** 标题变为可编辑的 input 框，当前标题作为初始值
- **AND** input 框自动获得焦点并选中文本

#### Scenario: 提交编辑

- **WHEN** 用户在编辑模式下按 Enter 键或使 input 失去焦点
- **THEN** 系统 SHALL 调用 `PATCH /api/explore/:id` 保存新标题
- **AND** 成功后标题显示更新后的文本
- **AND** session 列表缓存 SHALL 被刷新

#### Scenario: 取消编辑

- **WHEN** 用户在编辑模式下按 Escape 键
- **THEN** input 框退出编辑模式，标题恢复为原始值
- **AND** 不发送 API 请求

#### Scenario: 编辑为空值

- **WHEN** 用户清空标题并尝试提交
- **THEN** 标题恢复为原始值，不发送 API 请求

### Requirement: Crystallize 时用 issue 标题更新 session 标题

当 session 成功 crystallize 并创建 issue 时，系统 SHALL 用创建的 issue 标题更新 session 标题。

#### Scenario: Crystallize 创建新 issue 时更新标题

- **WHEN** session 通过 crystallize 创建了 issue #42，issue 标题为 "fix: pipeline retry logic"
- **THEN** session 标题 SHALL 更新为 "fix: pipeline retry logic"
- **AND** 前端 SHALL 收到更新后的 session 数据

#### Scenario: Crystallize 对已有 issue 操作时不覆盖

- **WHEN** session 已关联 issue #42，且执行 crystallize 对已有 issue 继续处理
- **THEN** session 标题 SHALL 保持不变（已有标题可能已被用户编辑过）

### Requirement: 前端 API client 和 hooks 支持标题更新

前端 `api.ts` SHALL 添加 `updateExploreSessionTitle` 方法，`useQueries.ts` SHALL 添加 `useUpdateExploreSessionTitle` mutation hook。

#### Scenario: updateExploreSessionTitle 调用

- **WHEN** 调用 `api.updateExploreSessionTitle(sessionId, "New Title")`
- **THEN** 发送 `PATCH /api/explore/:id` 请求，body 为 `{ title: "New Title" }`
- **AND** 返回更新后的 `ExploreSession` 对象

#### Scenario: useUpdateExploreSessionTitle mutation

- **WHEN** 组件调用 `useUpdateExploreSessionTitle()`
- **THEN** 成功时 SHALL 自动 invalidate `explore-sessions` 和 `explore/:id` 查询缓存
