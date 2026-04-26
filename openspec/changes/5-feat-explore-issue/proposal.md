## Why

Explore 当前是「一次性聊天」：创建 issue 后 session 被 crystallize 冻结，无法继续对话来完善需求；用户进入 /explore 后被自动重定向，看不到会话列表。需要将 Explore 升级为「Issue 打磨工具」，让 session 持续关联 Draft issue 并支持反复迭代。

## What Changes

- 新建 ExploreSessionList 页面替代 ExploreRedirect，显示所有 session 列表（标题、状态、关联 issue、更新时间）
- **BREAKING**: Session 状态简化为 `active | archived`，移除 `crystallized` 状态；`create_issue` tool 不再调 crystallize()，只调 updateIssueId()
- ExploreSessionRepo.create() 支持可选 issueId 参数，POST /explore API 接受 issueId
- ExploreSession 加 issueNumber 字段，GET /explore 列表 API join issues 表返回
- Agent prompt 动态注入 issueId 关联信息（Draft 可更新、非 Draft 只读、无 issue 可创建）
- 新增 update_issue tool，仅在 session 有 issueId 且 issue 为 Draft 时可用
- Draft issue 详情页增加 Explore 按钮（查找或创建关联 session 并跳转，同一 issue 只关联一个 session）
- ExplorePage 返回按钮改为导航到 /explore 列表
- Session 列表页支持删除操作（调用已有 DELETE /explore/:id）

## Capabilities

### New Capabilities

- `explore-session-list`: Session 列表页面组件，含创建、删除、关联 issue 展示
- `explore-issue-link`: Session 与 Issue 的双向关联（创建时指定 issueId、agent prompt 感知关联、update_issue tool）
- `issue-explore-entry`: Draft issue 详情页的 Explore 入口按钮

### Modified Capabilities

- `web-ui`: ExplorePage 返回按钮改为导航到列表页
- `http-api`: POST /explore 接受 issueId 参数；GET /explore 返回 issueNumber；session 状态模型变更

## Impact

- **后端**: ExploreSessionRepo（create 加参数、状态模型）、ExploreService（crystallize 逻辑移除）、explore API 路由、agent prompt 注入、新增 update_issue tool
- **前端**: 新增 ExploreSessionList 组件、修改 ExplorePage 返回导航、IssueDetailPage 加 Explore 按钮
- **数据**: session.status 值域变更（crystallized → active），向后兼容需处理已有 crystallized 记录
