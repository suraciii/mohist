## ADDED Requirements

### Requirement: mo otel query 直接查询 trace 数据库

CLI SHALL 提供 `mo otel query "<sql>"` 子命令，直接打开 `otel.db` 并以只读模式执行指定的 SQL 查询。该子命令 SHALL NOT 通过 HTTP 请求 server，SHALL 直接读取 SQLite 文件，因此 server 未运行时也能查询历史数据。命令 SHALL 默认从与 server 相同的数据目录（`~/.mohist/otel.db`）查找数据库，并 SHALL 支持通过 `-d <path>` / `--db <path>` 选项指定 `otel.db` 的完整路径。结果 SHALL 以表格形式输出到 stdout。

#### Scenario: 查询默认路径的数据库
- **WHEN** 用户执行 `mo otel query "SELECT COUNT(*) FROM traces"`
- **AND** `~/.mohist/otel.db` 存在
- **THEN** CLI SHALL 直接读取 `~/.mohist/otel.db`
- **AND** SHALL 以只读模式执行 SQL
- **AND** SHALL 将结果以表格输出到 stdout

#### Scenario: 指定自定义数据库路径
- **WHEN** 用户执行 `mo otel query "SELECT * FROM traces" -d /tmp/custom-otel.db`
- **THEN** CLI SHALL 打开 `/tmp/custom-otel.db`
- **AND** SHALL 在该数据库上执行查询

#### Scenario: server 未运行时仍可查询
- **WHEN** 用户执行 `mo otel query "..."`
- **AND** server 未运行
- **THEN** CLI SHALL 正常执行查询并输出结果
- **AND** CLI SHALL NOT 因 server 不可用而报错

#### Scenario: 数据库文件不存在
- **WHEN** 用户执行 `mo otel query "..."`
- **AND** 默认路径或 `-d` 指定的 `otel.db` 不存在
- **THEN** CLI SHALL 以非零 exit code 退出
- **AND** SHALL 输出明确的错误信息说明数据库文件未找到

#### Scenario: SQL 执行错误
- **WHEN** 用户执行 `mo otel query "SELECT * FROM nonexistent"`
- **AND** 数据库存在但 SQL 引用不存在的表
- **THEN** CLI SHALL 以非零 exit code 退出
- **AND** SHALL 输出 SQLite 返回的错误信息

#### Scenario: 缺少 SQL 参数
- **WHEN** 用户执行 `mo otel query` 不带 SQL 参数
- **THEN** CLI SHALL 以非零 exit code 退出
- **AND** SHALL 提示需要 SQL 查询参数

### Requirement: mo otel status 通过 HTTP 探测 collector 状态

CLI SHALL 提供 `mo otel status` 子命令，通过 HTTP 请求主 server 的 `GET /otel/api/status` 端点判断 collector 是否在线，并显示数据库大小与 trace 条数统计。该子命令 SHALL 需要 server 运行；server 未运行时 SHALL 输出明确的错误信息（遵循 cli-interface 的 server 检测约定），而不是抛出网络异常堆栈。

#### Scenario: collector 在线
- **WHEN** 用户执行 `mo otel status`
- **AND** server 运行中，且 OTLP ingestion 端口正常监听
- **THEN** CLI SHALL 输出 collector 状态为在线
- **AND** SHALL 输出数据库大小、trace 条数、span 条数

#### Scenario: collector 离线但 server 在线
- **WHEN** 用户执行 `mo otel status`
- **AND** server 运行中，但 OTLP ingestion 端口未启动
- **THEN** CLI SHALL 输出 collector 状态为离线
- **AND** SHALL 仍显示可用的数据库统计信息

#### Scenario: server 未运行
- **WHEN** 用户执行 `mo otel status`
- **AND** server 未运行
- **THEN** CLI SHALL 输出 "Server is not running. Start with: mo server start"
- **AND** SHALL 以非零 exit code 退出
- **AND** SHALL NOT 输出原始 ECONNREFUSED 堆栈

### Requirement: mo otel 命令组提供 help 与子命令发现

CLI SHALL 提供 `mo otel` 顶层命令组，包含 `query` 与 `status` 子命令。`mo otel --help` SHALL 列出所有子命令及其简要说明。

#### Scenario: 查看 otel 命令组 help
- **WHEN** 用户执行 `mo otel --help`
- **THEN** SHALL 列出 `query` 与 `status` 子命令
- **AND** 每个子命令 SHALL 附带简要说明

#### Scenario: 无子命令时提示 help
- **WHEN** 用户执行 `mo otel` 不带子命令
- **THEN** SHALL 输出命令组用法（等价于 `mo otel --help`）
