## ADDED Requirements

### Requirement: NotFoundPage 404 组件

WebUI SHALL 提供一个 `NotFoundPage` 组件，当用户访问不存在的路由或请求不存在的资源时显示 404 页面。页面 SHALL 包含错误提示文案和返回首页的导航链接。

#### Scenario: 访问无效路径显示 404 页面
- **WHEN** 用户访问未定义的路由（如 `/foo`、`/issue/abc/invalid`）
- **THEN** 显示 404 页面，包含 "Page not found" 提示
- **AND** 显示可点击的 "Back to board" 链接，导航到 `/`

#### Scenario: 404 页面不触发全页面刷新
- **WHEN** 用户点击 404 页面上的 "Back to board" 链接
- **THEN** 使用 React Router 客户端导航到 `/`
- **AND** 不发生浏览器全页面刷新
