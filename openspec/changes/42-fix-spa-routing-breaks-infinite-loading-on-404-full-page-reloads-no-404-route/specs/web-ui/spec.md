## ADDED Requirements

### Requirement: Issue 详情页处理 API 错误

IssueDetailPage SHALL 处理 `useIssue` 返回的错误状态。当 API 返回 404 或其他错误时，SHALL 显示 NotFoundPage 组件，而非无限显示 "Loading..."。

#### Scenario: 访问不存在的 issue 显示 404
- **WHEN** 用户访问 `/issue/99999`
- **AND** API 返回 404
- **THEN** 页面显示 NotFoundPage 组件
- **AND** 不显示 "Loading..." 文本

#### Scenario: API 返回其他错误时显示 404
- **WHEN** 用户访问 `/issue/:number`
- **AND** API 返回非 404 的错误（如 500）
- **THEN** 页面显示 NotFoundPage 组件
- **AND** 不显示 "Loading..." 文本

### Requirement: IssueCard 使用 SPA 导航

IssueCard SHALL 使用 React Router 的 `<Link>` 组件进行导航，而非原生 `<a>` 标签，以避免全页面刷新。

#### Scenario: 点击 Issue 卡片导航到详情页
- **WHEN** 用户在看板页面点击某个 Issue 卡片
- **THEN** 使用 React Router 客户端导航到 `/issue/:number`
- **AND** 不发生浏览器全页面刷新
- **AND** 浏览器 URL 更新为目标路径

### Requirement: 路由配置包含 catch-all 404 路由

App.tsx 的路由配置 SHALL 包含一个 catch-all `*` 路由作为最后一条路由规则，渲染 NotFoundPage 组件。

#### Scenario: 未匹配路径显示 404
- **WHEN** 用户访问 `/unknown-path`
- **AND** 该路径不匹配任何已定义的路由
- **THEN** 显示 NotFoundPage 组件

#### Scenario: 有效路径不受 catch-all 影响
- **WHEN** 用户访问 `/`、`/issue/1`、`/settings`、`/logs`、`/explore` 等有效路径
- **THEN** 正常渲染对应页面组件
- **AND** 不显示 NotFoundPage
