## MODIFIED Requirements

### Requirement: Web UI 提供日志查看页面

Web UI SHALL 提供 `/logs` 路由，显示结构化日志查看页面。导航入口 SHALL 根据视口宽度适配：桌面端通过 Header 导航链接，移动端通过底部 Tab 导航栏。

#### Scenario: 桌面端导航到日志页面
- **WHEN** 视口宽度 >= 768px
- **AND** 用户点击 Header 中的 "Logs" 导航链接
- **THEN** 导航到 `/logs` 路由
- **AND** 显示日志查看页面

#### Scenario: 移动端导航到日志页面
- **WHEN** 视口宽度 < 768px
- **AND** 用户点击底部 Tab 导航栏的 "Settings" Tab
- **THEN** 导航到 `/settings` 路由
- **AND** Logs 页面通过 Settings 页面内的导航链接访问
