## ADDED Requirements

### Requirement: Explore 页面安装 typography 插件
WebUI SHALL 安装 `@tailwindcss/typography` 并在 CSS 入口通过 `@plugin` 指令引入，使 `prose` 类正确渲染 Markdown 内容的排版样式（标题、段落、列表、代码块等）。

#### Scenario: Markdown 内容正确渲染
- **WHEN** explore agent 返回包含 Markdown 的消息
- **THEN** 消息内容通过 `react-markdown` 渲染
- **AND** `prose` 类提供正确的排版样式（标题大小、段落间距、列表样式、代码块背景）

### Requirement: Explore 页面展示流式错误
`useExploreStream` hook SHALL 暴露 `streamError` 状态。当 SSE `done` 事件包含 error 字段或流读取发生异常时，SHALL 设置 `streamError`。ExplorePage SHALL 在有错误时显示错误提示。

#### Scenario: 后端返回流错误
- **WHEN** explore agent 运行失败
- **AND** SSE 发送 `done` 事件包含 `error` 字段
- **THEN** `useExploreStream` 设置 `streamError` 为错误信息
- **AND** ExplorePage 显示错误提示（红色背景框）
- **AND** 用户下次发送消息时 `streamError` 被清除

#### Scenario: 网络异常导致流中断
- **WHEN** 流读取过程中发生网络错误
- **THEN** `useExploreStream` 设置 `streamError` 为错误信息
- **AND** ExplorePage 显示错误提示

### Requirement: Explore 重定向处理创建失败
`/explore` 路由的自动重定向 SHALL 处理 session 创建失败，显示错误提示而非无限 loading。

#### Scenario: Session 创建失败
- **WHEN** 用户访问 `/explore`（无 active session）
- **AND** `POST /api/explore` 请求失败
- **THEN** 显示错误提示信息
- **AND** 不进入无限 loading 状态
