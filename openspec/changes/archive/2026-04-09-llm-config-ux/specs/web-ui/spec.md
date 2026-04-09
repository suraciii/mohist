## ADDED Requirements

### Requirement: Explore 页面 LLM 未配置时显示引导卡片

Explore 页面 SHALL 在加载时检查 LLM 配置状态。当 LLM 未配置时，页面 SHALL 显示引导卡片替代聊天界面，引导用户配置 provider。

引导卡片 SHALL 包含：
- 问题说明（LLM provider 未配置）
- 配置文件路径（`~/.mohist/config.jsonc`）
- 配置示例（包含 anthropic 和 glm 的 JSONC 示例，含注释说明）
- 支持的 provider 列表（anthropic、glm、openai 等）
- 刷新按钮（重新查询 status API）

#### Scenario: LLM 未配置时显示引导卡片
- **WHEN** 用户进入 Explore 页面
- **AND** `/api/status` 返回 `llm.configured === false`
- **THEN** 页面显示引导卡片，不显示聊天输入框
- **AND** 卡片包含配置文件路径和配置示例

#### Scenario: LLM 已配置时正常显示
- **WHEN** 用户进入 Explore 页面
- **AND** `/api/status` 返回 `llm.configured === true`
- **THEN** 页面正常显示聊天界面

#### Scenario: 引导卡片刷新按钮
- **WHEN** 用户在引导卡片中点击刷新按钮
- **THEN** 重新请求 `/api/status`
- **AND** 如果 `llm.configured === true`，切换到正常聊天界面
- **AND** 如果仍返回 `false`，保持引导卡片显示

#### Scenario: Status 请求失败时不阻塞
- **WHEN** `/api/status` 请求失败
- **THEN** Explore 页面 SHALL 按原有逻辑正常显示，不因 status 失败而阻塞
- **WHEN** `/api/status` 请求失败
- **THEN** Explore 页面 SHALL 按原有逻辑正常显示，不因 status 失败而阻塞

### Requirement: Explore 页面展示分类错误提示

当 Explore 消息发送失败时，前端 SHALL 根据 API 返回的 `code` 字段展示不同的错误提示。

#### Scenario: LLM_NOT_CONFIGURED 错误
- **WHEN** Explore 消息发送失败
- **AND** API 返回 `{ code: "LLM_NOT_CONFIGURED" }`
- **THEN** 错误提示 SHALL 显示 LLM 未配置的引导信息
- **AND** 包含配置方法说明

#### Scenario: 无 code 字段的错误
- **WHEN** Explore 消息发送失败
- **AND** API 响应无 `code` 字段
- **THEN** 保持原有错误展示逻辑（显示 `error.message`）
