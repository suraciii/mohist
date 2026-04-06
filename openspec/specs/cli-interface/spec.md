## Requirements

### Requirement: CLI 提供分组式命令界面

CLI SHALL 提供分组式命令界面，与 server 通信。

#### Scenario: 查看 help
- **WHEN** 用户执行 `mo --help`
- **THEN** 显示所有命令组和用法

#### Scenario: 查看子命令 help
- **WHEN** 用户执行 `mo issue --help`
- **THEN** 显示 issue 命令组的所有子命令

### Requirement: CLI 是 thin client

CLI SHALL NOT 包含业务逻辑，所有逻辑在 server 侧。

#### Scenario: CLI 调用 server API
- **WHEN** 用户执行 `mo issue list`
- **THEN** CLI 调用 `GET /api/issues`
- **AND** CLI 格式化输出 server 返回的数据
- **AND** CLI 不做任何业务决策

#### Scenario: CLI 不存储状态
- **WHEN** CLI 执行任何命令
- **THEN** CLI 不在本地存储任何业务状态
- **AND** 所有状态由 server 管理

### Requirement: CLI 检测 server 状态

CLI SHALL 在执行命令前检测 server 是否运行。

#### Scenario: Server 未运行
- **WHEN** 用户执行需要 server 的命令
- **AND** server 未运行
- **THEN** CLI 返回错误 "Server is not running"
- **AND** CLI 提示 "Start with: mo server start"

#### Scenario: Server 运行中
- **WHEN** 用户执行需要 server 的命令
- **AND** server 运行中
- **THEN** CLI 正常调用 server API

### Requirement: CLI 提供美化的输出

CLI SHALL 提供清晰、美化的终端输出。

#### Scenario: status 命令输出
- **WHEN** 用户执行 `mo status`
- **THEN** 显示格式化的状态表格
- **AND** 显示当前项目名称
- **AND** 使用颜色区分不同状态

#### Scenario: 错误信息友好
- **WHEN** 命令执行失败
- **THEN** 显示清晰的错误信息
- **AND** 提供可能的解决方案

### Requirement: CLI 支持本地 Issue CRUD

CLI SHALL 通过 Server API 支持本地 Issue 的创建、读取、更新、删除操作。

#### Scenario: CLI 调用 Server API 创建 Issue
- **WHEN** 用户执行 `mo issue create "title"`
- **THEN** CLI 发送 POST /api/issues 请求到 Server
- **AND** Server 在本地 SQLite 创建 Issue
- **AND** CLI 显示创建结果

#### Scenario: CLI 调用 Server API 列出 Issues
- **WHEN** 用户执行 `mo issue list`
- **THEN** CLI 发送 GET /api/issues 请求到 Server
- **AND** Server 从本地 SQLite 查询 Issues
- **AND** CLI 格式化显示结果

#### Scenario: CLI 调用 Server API 更新 Issue
- **WHEN** 用户执行 `mo issue update <id> --title "new"`
- **THEN** CLI 发送 PATCH /api/issues/:id 请求到 Server
- **AND** Server 更新本地 SQLite
- **AND** CLI 显示更新结果

#### Scenario: CLI 调用 Server API 添加评论
- **WHEN** 用户执行 `mo issue comment <id> "text"`
- **THEN** CLI 发送 POST /api/issues/:id/comments 请求到 Server
- **AND** Server 在本地 SQLite 创建 comment
- **AND** CLI 显示成功消息

### Requirement: Server API 扩展

Server SHALL 新增以下 API 端点支持本地 Issue CRUD。

#### Scenario: POST /api/issues
- **WHEN** Server 收到 POST /api/issues 请求
- **WITH** body: { title, body?, labels? }
- **THEN** Server 在当前项目创建 Issue
- **AND** 返回 Issue 详情

#### Scenario: PATCH /api/issues/:id
- **WHEN** Server 收到 PATCH /api/issues/:id 请求
- **WITH** body: { title?, body?, labels? }
- **THEN** Server 更新指定 Issue
- **AND** 返回更新后的 Issue

#### Scenario: POST /api/issues/:id/comments
- **WHEN** Server 收到 POST /api/issues/:id/comments 请求
- **WITH** body: { body }
- **THEN** Server 创建 comment
- **AND** 返回 comment 详情

#### Scenario: GET /api/labels
- **WHEN** Server 收到 GET /api/labels 请求
- **THEN** Server 返回当前项目所有使用过的 labels
- **AND** 按名称排序

### Requirement: CLI 支持 server 命令

CLI SHALL 支持 server 管理命令（无需 server 运行）。

#### Scenario: 启动 server
- **WHEN** 用户执行 `mo server start`
- **THEN** CLI 启动 server 进程
- **AND** CLI 等待 server 就绪
- **AND** CLI 显示 "Server started"

#### Scenario: 停止 server
- **WHEN** 用户执行 `mo server stop`
- **THEN** CLI 发送停止信号给 server
- **AND** CLI 显示 "Server stopped"

### Requirement: CLI removes dead workflow commands

The CLI SHALL NOT expose `issue approve`, `issue pause`, or `issue resume` commands. The `issue show` command SHALL NOT display `progress` or `stageInfo` (these fields are removed from the API response). The `formatStage()` function SHALL NOT map `waiting-design-review` or `waiting-review`.

#### Scenario: Dead commands not available
- **WHEN** user executes `mo issue --help`
- **THEN** the output SHALL NOT list `approve`, `pause`, or `resume` subcommands

#### Scenario: Dead command attempted
- **WHEN** user executes `mo issue approve`, `mo issue pause`, or `mo issue resume`
- **THEN** CLI SHALL display an unknown command error

#### Scenario: Issue show omits progress display
- **WHEN** user executes `mo issue show <number>`
- **THEN** the output SHALL NOT display progress bar or stage info block

### Requirement: Server status CLI omits task/worker info

The `mo server status` command SHALL NOT display `Workers`, `Running tasks`, or `Queued tasks` lines. The `fetchServerStatus()` function SHALL NOT reference removed status fields.

#### Scenario: Server status display
- **WHEN** user executes `mo server status` and server is running
- **THEN** the output SHALL NOT include lines about workers, running tasks, or queued tasks

### Requirement: CLI 共享 apiClient 实现

CLI 命令模块 SHALL 共享同一个 `apiClient` 实现，不各自定义重复版本。公共模块位于 `cli/api-client.ts`。

#### Scenario: 所有命令模块使用共享 apiClient
- **WHEN** 检查 `cli/commands/issue.ts`、`cli/commands/quick.ts`、`cli/commands/project.ts`
- **THEN** 均从 `../api-client` 导入 `apiClient` 函数
- **AND** 无文件内定义本地的 `apiClient` 函数
- **AND** 无文件内定义本地的 `API_BASE` 常量

#### Scenario: apiClient 行为不变
- **WHEN** CLI 通过共享 `apiClient` 调用 server API
- **THEN** 行为与重构前完全一致（HTTP 请求、JSON 解析、错误处理）
