## ADDED Requirements

### Requirement: mo providers list 命令

CLI SHALL 提供 `mo providers list`（别名 `mo providers ls`）命令，列出所有已配置和内置的 provider 状态。

每个 provider SHALL 显示：
- providerID
- 显示名称
- 状态：已配置（有 apiKey）或 未配置
- apiKey（掩码显示，如 `sk-***...xyz`）
- baseURL（如果有自定义）
- 来源：config 文件 或 环境变量

#### Scenario: 列出所有 provider
- **WHEN** 用户执行 `mo providers list`
- **THEN** 显示所有内置 provider 的状态表格
- **AND** 已配置的 provider 显示掩码后的 apiKey
- **AND** 未配置的 provider 显示 "not configured"

#### Scenario: 有环境变量 provider
- **WHEN** 用户执行 `mo providers list`
- **AND** `ANTHROPIC_API_KEY` 环境变量已设置
- **AND** config.jsonc 中无 anthropic 配置
- **THEN** anthropic 行 SHALL 显示状态为 "configured (env)"
- **AND** apiKey 列显示掩码后的值

### Requirement: mo providers login 命令

CLI SHALL 提供 `mo providers login <providerID>` 命令，交互式设置 provider 的 API key。

- SHALL 接受 providerID 作为参数
- SHALL 显示 provider 的显示名称和说明
- SHALL 提示用户输入 API key（密码模式，不回显）
- SHALL 将 API key 写入 `~/.mohist/config.jsonc` 的 `provider.<providerID>.apiKey` 字段
- 如果 config.jsonc 不存在 SHALL 创建
- 如果 provider 段不存在 SHALL 创建
- 写入成功后 SHALL 显示确认信息

#### Scenario: 登录内置 provider
- **WHEN** 用户执行 `mo providers login glm`
- **THEN** 显示 "智谱 GLM (https://open.bigmodel.cn/api/paas/v4)"
- **AND** 提示 "API Key: "（密码输入模式）
- **AND** 输入后写入 config.jsonc
- **AND** 显示 "✓ Saved to ~/.mohist/config.jsonc"

#### Scenario: 登录自定义 provider
- **WHEN** 用户执行 `mo providers login my-custom-llm`
- **AND** "my-custom-llm" 不在内置注册表中
- **THEN** SHALL 提示输入 API Key 和 baseURL
- **AND** 写入 config.jsonc 的 `provider.my-custom-llm` 段（含 apiKey 和 baseURL）

#### Scenario: 覆盖已有 key
- **WHEN** 用户执行 `mo providers login openai`
- **AND** config.jsonc 中已有 `provider.openai.apiKey`
- **THEN** SHALL 提示 "API Key (currently sk-***...xyz): "
- **AND** 输入新值后覆盖旧值

#### Scenario: 无参数调用
- **WHEN** 用户执行 `mo providers login`（无 providerID）
- **THEN** 显示错误信息 "Usage: mo providers login <providerID>"
- **AND** 列出可用的内置 provider

### Requirement: mo providers logout 命令

CLI SHALL 提供 `mo providers logout <providerID>` 命令，删除 provider 的 API key 配置。

- SHALL 从 config.jsonc 中删除 `provider.<providerID>.apiKey` 字段
- 如果 apiKey 是该 provider 段最后一个字段，SHALL 删除整个 provider 段
- 删除成功后 SHALL 显示确认信息

#### Scenario: 登出已有 provider
- **WHEN** 用户执行 `mo providers logout glm`
- **AND** config.jsonc 中有 `provider.glm.apiKey`
- **THEN** SHALL 删除 apiKey 字段
- **AND** 显示 "✓ Removed glm credentials from ~/.mohist/config.jsonc"

#### Scenario: 登出未配置的 provider
- **WHEN** 用户执行 `mo providers logout glm`
- **AND** config.jsonc 中无 glm 配置
- **THEN** 显示 "Provider 'glm' is not configured"

### Requirement: mo providers help 命令

CLI SHALL 提供 `mo providers --help` 显示 providers 命令组的用法说明。

#### Scenario: 查看 help
- **WHEN** 用户执行 `mo providers --help`
- **THEN** 显示 providers 命令组描述和所有子命令（list, login, logout）

### Requirement: providers 命令不需要 Server 运行

`mo providers` 命令组 SHALL 直接读写 `~/.mohist/config.jsonc`，不需要 mohist server 运行。

#### Scenario: Server 未运行时执行 providers 命令
- **WHEN** mohist server 未运行
- **AND** 用户执行 `mo providers list`
- **THEN** 命令 SHALL 正常执行
- **AND** 不显示 "Server is not running" 错误
