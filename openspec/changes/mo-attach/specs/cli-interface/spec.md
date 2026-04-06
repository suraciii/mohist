## NEW Requirements

### Capability: mo-attach

CLI 命令，连接 server SSE 端点，实时渲染 agent 执行事件到终端。

#### Requirement: mo attach 命令连接 SSE 端点

`mo attach` SHALL 连接到 mohist server 的 SSE 端点，订阅事件流，将事件格式化输出到终端。

##### Scenario: 基本监控
- **WHEN** 用户运行 `mo attach`
- **THEN** 连接到 server SSE 端点
- **AND** 实时显示所有 agent 相关事件（started, paused, completed, error, stage_changed, comment_added, approval_requested）
- **AND** 每个事件包含时间戳、事件类型、issue 编号和相关数据

##### Scenario: 项目过滤
- **WHEN** 用户运行 `mo attach --project myapp`
- **THEN** 只显示 myapp 项目的事件

##### Scenario: 自动重连
- **WHEN** 用户运行 `mo attach --follow`
- **AND** SSE 连接断开
- **THEN** 2 秒后自动重连
- **AND** 打印 "Reconnecting..." 提示

##### Scenario: server 未运行
- **WHEN** 用户运行 `mo attach`
- **AND** mohist server 未运行
- **THEN** 显示友好错误信息 "Server is not running. Start with: mo server start"

##### Scenario: 优雅退出
- **WHEN** 用户按 Ctrl+C
- **THEN** 关闭 SSE 连接
- **AND** 打印 "Detached." 后退出
