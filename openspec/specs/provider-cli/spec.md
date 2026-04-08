## Requirements

### Requirement: CLI 提供 provider 管理命令

CLI SHALL 提供 `mo providers` 命令组用于管理 LLM provider 配置。

#### Scenario: 列出所有 provider
- **WHEN** 用户执行 `mo providers list` 或 `mo providers ls`
- **THEN** CLI 显示所有内置 provider 的状态表格
- **AND** 显示 provider ID、名称、配置状态、API Key（掩码）、baseURL

#### Scenario: 查看 providers 帮助
- **WHEN** 用户执行 `mo providers --help`
- **THEN** 显示 providers 命令组的所有子命令说明

### Requirement: Provider 登录（配置 API Key）

CLI SHALL 提供 `mo providers login <providerID>` 命令交互式配置 provider API Key。

#### Scenario: 登录内置 provider
- **WHEN** 用户执行 `mo providers login anthropic`
- **THEN** CLI 提示输入 API Key（隐藏输入）
- **AND** 保存到 `~/.mohist/config.jsonc`
- **AND** 显示确认信息

#### Scenario: 登录自定义 provider
- **WHEN** 用户执行 `mo providers login my-custom`
- **AND** "my-custom" 不是内置 provider
- **THEN** CLI 提示输入 API Key 和 Base URL
- **AND** 自动设置 sdk 类型为 `openai-compatible`
- **AND** 保存到 `~/.mohist/config.jsonc`

#### Scenario: 覆盖已配置的 provider
- **WHEN** 用户执行 `mo providers login <providerID>` 且该 provider 已配置
- **THEN** CLI 显示当前已配置的 API Key（掩码）
- **AND** 允许用户输入新的 API Key 覆盖

### Requirement: Provider 登出（删除配置）

CLI SHALL 提供 `mo providers logout <providerID>` 命令删除 provider 配置。

#### Scenario: 登出已配置的 provider
- **WHEN** 用户执行 `mo providers logout anthropic`
- **AND** anthropic 已在 config.jsonc 中配置
- **THEN** CLI 从 config.jsonc 删除该 provider 的 apiKey
- **AND** 如果 provider 段无其他字段，删除整个 provider 段
- **AND** 显示确认信息

#### Scenario: 登出未配置的 provider
- **WHEN** 用户执行 `mo providers logout anthropic`
- **AND** anthropic 未在 config.jsonc 中配置
- **THEN** CLI 显示警告信息："Provider 'anthropic' is not configured"

### Requirement: API Key 安全显示

CLI SHALL 在显示 API Key 时使用掩码保护隐私。

#### Scenario: 掩码格式
- **WHEN** CLI 需要显示 API Key "sk-abc123xyz789"
- **THEN** 显示为 "sk-***...789"（保留前 3 和后 3 字符）
- **AND** 短于 6 字符的 key 显示为 "***"
