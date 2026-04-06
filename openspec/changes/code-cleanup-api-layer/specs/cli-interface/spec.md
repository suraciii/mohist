## ADDED Requirements

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
