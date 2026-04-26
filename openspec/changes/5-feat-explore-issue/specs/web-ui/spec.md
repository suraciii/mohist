## MODIFIED Requirements

### Requirement: 无项目时显示空状态引导

WebUI SHALL 在没有项目时显示空状态引导页面，替代看板视图和 "Loading..." 文本。

#### Scenario: 首次访问无项目
- **WHEN** 用户打开 WebUI
- **AND** 项目列表为空
- **THEN** 显示空状态页面，包含提示文字 "No projects yet"
- **AND** 显示 "Create Project" 按钮

#### Scenario: 从空状态创建项目
- **WHEN** 用户在空状态页面点击 "Create Project"
- **THEN** 弹出 `CreateProjectDialog`
- **AND** 创建成功后自动切换到看板视图

#### Scenario: 无项目时访问 Explore 页面
- **WHEN** 用户访问 `/explore` 路由
- **AND** 项目列表为空
- **THEN** SHALL 显示空状态引导页面（与首页一致的引导）
- **AND** 不 SHALL 显示 "Loading..." 文本

## ADDED Requirements

### Requirement: ExplorePage 返回按钮导航到列表页

ExplorePage header 的返回按钮 SHALL 导航到 `/explore`（session 列表页），而非 `/`（首页）。

#### Scenario: 点击返回按钮回到列表
- **WHEN** 用户在 `/explore/:id` 页面点击 header 返回按钮
- **THEN** 导航到 `/explore`（session 列表页）

### Requirement: ExplorePage 显示关联 issue 信息

ExplorePage header 区域 SHALL 在 session 关联了 issue 时显示关联的 issue 编号。

#### Scenario: session 关联 issue 时显示编号
- **WHEN** 用户查看 explore session 详情页
- **AND** session 关联了 issue（issueNumber 存在）
- **THEN** header 区域显示 "Issue #N" 链接，点击可导航到 issue 详情页

#### Scenario: session 未关联 issue 时不显示
- **WHEN** 用户查看 explore session 详情页
- **AND** session 未关联 issue
- **THEN** header 区域不显示 issue 相关信息
