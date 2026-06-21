## ADDED Requirements

### Requirement: CLI 提供 mo otel 命令组

CLI SHALL 提供顶层 `mo otel` 命令组，用于查询本地 OTel trace 数据与检查 collector 状态。该命令组 SHALL 至少包含 `query` 与 `status` 子命令，并在 `mo --help` 顶层输出中可见。`query` 子命令 SHALL 直接读取 SQLite 文件、不需要 server 运行；`status` 子命令 SHALL 通过 HTTP 请求 server、需要 server 运行。

各子命令的详细行为由 `otel-cli` capability 定义。

#### Scenario: otel 命令组出现在顶层 help
- **WHEN** 用户执行 `mo --help`
- **THEN** 输出 SHALL 包含 `otel` 命令组及其简要说明

#### Scenario: otel 子命令不需要统一的 server 状态前置检查
- **WHEN** 用户执行 `mo otel query`
- **THEN** CLI SHALL NOT 在执行前要求 server 运行（因为 `query` 直接读 SQLite）
- **AND** 仅 `mo otel status` 子命令 SHALL 要求 server 运行
