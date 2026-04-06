## NEW Requirements

### Capability: mo-attach

CLI 命令，连接 server SSE 端点，实时渲染 agent 执行事件到终端。

#### Requirement: mo attach 命令连接 SSE 端点

`mo attach` SHALL 连接到 mohist server 的 SSE 端点 `/api/events`，订阅事件流，将事件格式化输出到终端。

##### Scenario: 基本监控
- **WHEN** 用户运行 `mo attach`
- **THEN** 连接到 server SSE 端点
- **AND** 实时显示所有 agent 相关事件
- **AND** 每个事件包含时间戳、事件类型、issue 编号和相关数据

支持的事件类型（7 种）：
- `agent_started` - agent 开始执行
- `agent_completed` - agent 正常完成
- `agent_paused` - agent 暂停等待用户输入
- `agent_error` - agent 执行出错
- `stage_changed` - 阶段变更
- `comment_added` - 添加评论
- `approval_requested` - 请求审批

##### Scenario: 项目过滤
- **WHEN** 用户运行 `mo attach --project myapp`
- **THEN** 查询 `/api/projects` 解析 myapp 为 project ID
- **AND** 连接到 SSE 端点并添加 `?projectId=<id>` 参数
- **AND** 只显示该项目的 agent 事件

##### Scenario: 使用当前项目
- **WHEN** 用户运行 `mo attach`（不带 --project）
- **AND** 当前目录在 mohist 项目中
- **THEN** 使用当前项目的 projectId 过滤事件
- **AND** 如果不在项目目录中，显示所有项目的事件

##### Scenario: 自动重连
- **WHEN** 用户运行 `mo attach --follow`
- **AND** SSE 连接断开
- **THEN** 打印 "Reconnecting..." 提示
- **AND** 等待 2 秒后自动重连
- **AND** 继续接收新事件（可能收到重复事件）

**注意**: 不使用 Last-Event-ID 断点续传。重连时从最新事件开始接收。

##### Scenario: server 未运行
- **WHEN** 用户运行 `mo attach`
- **AND** mohist server 未运行
- **THEN** 显示错误信息 "Error: Server is not running"
- **AND** 显示提示 "Start the server with: mo server start"
- **AND** 退出状态码 1

##### Scenario: 优雅退出
- **WHEN** 用户按 Ctrl+C 或进程收到 SIGTERM
- **THEN** 关闭 SSE 连接
- **AND** 打印 "Detached."
- **AND** 正常退出（状态码 0）

##### Scenario: 未知事件类型
- **WHEN** 收到未定义的事件类型
- **THEN** 打印原始事件数据（event type + data）
- **AND** 继续处理后续事件

#### Requirement: 后端事件订阅修复

后端 SHALL 将 `agent_paused` 添加到 SSE 事件订阅列表，使 pause 事件能够发送到客户端。

##### Scenario: agent_paused 事件可见
- **WHEN** agent 执行到暂停点
- **THEN** `agent_paused` 事件通过 SSE 发送到所有连接的客户端
- **AND** 客户端显示 `|| agent paused` 消息
