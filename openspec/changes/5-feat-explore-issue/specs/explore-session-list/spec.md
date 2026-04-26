## ADDED Requirements

### Requirement: Session 列表页替代 ExploreRedirect

`/explore` 路由 SHALL 显示 ExploreSessionList 组件替代 ExploreRedirect 自动跳转。组件 SHALL 展示当前项目的所有 explore session。

#### Scenario: 访问 /explore 显示列表页
- **WHEN** 用户导航到 `/explore`
- **AND** 当前项目存在
- **THEN** 显示 ExploreSessionList 组件
- **AND** 不发生自动重定向

#### Scenario: 列表展示 session 信息
- **WHEN** ExploreSessionList 渲染
- **THEN** 每个 session 卡片显示：标题、状态（active/archived）、关联 issue 编号（如有）、更新时间
- **AND** session 按更新时间倒序排列

#### Scenario: 点击 session 进入对话
- **WHEN** 用户点击某个 session 卡片
- **THEN** 导航到 `/explore/:id`

#### Scenario: 无 session 时显示空状态
- **WHEN** 当前项目没有任何 session
- **THEN** 显示空状态提示
- **AND** 显示 "New Session" 按钮

### Requirement: Session 列表页创建新 session

ExploreSessionList 页面 SHALL 在顶部提供 "New Session" 按钮，点击后创建新 session 并导航到该 session。

#### Scenario: 点击 New Session 创建并跳转
- **WHEN** 用户点击 "New Session" 按钮
- **THEN** 调用 `POST /api/explore` 创建新 session（无 issueId）
- **AND** 创建成功后导航到 `/explore/:id`

#### Scenario: 创建失败显示错误
- **WHEN** 创建 session 请求失败
- **THEN** 显示错误提示
- **AND** 保留在列表页面

### Requirement: Session 列表页删除 session

每个 session 卡片 SHALL 提供删除操作。删除前 SHALL 显示确认对话框。删除 session 不影响关联的 issue。

#### Scenario: 删除 session 需确认
- **WHEN** 用户点击 session 卡片的删除按钮
- **THEN** 显示确认对话框

#### Scenario: 确认删除 session
- **WHEN** 用户在确认对话框中点击确认
- **THEN** 调用 `DELETE /api/explore/:id`
- **AND** 从列表中移除该 session
- **AND** 关联的 issue 不受影响

#### Scenario: 取消删除
- **WHEN** 用户在确认对话框中点击取消
- **THEN** 对话框关闭，session 保留在列表中
