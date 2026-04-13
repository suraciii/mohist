## MODIFIED Requirements

### Requirement: Model ID 解析

系统 SHALL 将 model 字符串按第一个 `/` 分割为 providerID 和 modelID。格式为 `"provider/model-id"`。所有存储、传输、显示位置 SHALL 统一使用此全限定格式，且 **providerID 直接使用 models.dev 中的 provider ID**。

#### Scenario: 标准格式
- **WHEN** model 字符串为 "minimax/MiniMax-M2.7"
- **THEN** providerID SHALL 为 "minimax"，modelID SHALL 为 "MiniMax-M2.7"

#### Scenario: 无效格式（裸 ID）
- **WHEN** model 字符串为 "MiniMax-M2.7"（无斜杠）
- **THEN** resolveModel() SHALL 抛出错误，提示期望 "provider/model-id" 格式

#### Scenario: 空 provider 或空 model
- **WHEN** model 字符串为 "/model" 或 "provider/"
- **THEN** resolveModel() SHALL 抛出错误

### Requirement: 默认模型

当 config.jsonc 中未设置 `model` 字段时，系统 SHALL 自动从已配置 provider 中选择最新模型（见 smart-default-model spec）。移除硬编码默认模型 `"anthropic/claude-sonnet-4-20250514"`。

#### Scenario: 无 model 配置
- **WHEN** config.jsonc 不存在或无 `model` 字段
- **THEN** resolveModel() SHALL 使用智能默认模型选择逻辑（按 release_date 从已配置 provider 中选择最新模型）

#### Scenario: 有 model 配置
- **WHEN** config.jsonc 中 `model` 字段为 `"minimax/MiniMax-M2.5"`
- **THEN** resolveModel() SHALL 使用显式配置的模型

### Requirement: Explore session model ID 存储

`POST /explore/:id/model` 接口 SHALL 接收并存储全限定 model ID（`provider/model-id` 格式）。验证逻辑只接受全限定 ID；裸 ID SHALL 视为无效。

#### Scenario: 传全限定 model ID
- **WHEN** 请求体为 `{ model: "minimax/MiniMax-M2.7" }`
- **THEN** 系统 SHALL 验证通过并存储 `"minimax/MiniMax-M2.7"`

#### Scenario: 传裸 model ID
- **WHEN** 请求体为 `{ model: "MiniMax-M2.7" }`
- **THEN** 系统 SHALL 返回 400 错误 `"Invalid model format: expected provider/model-id"`

#### Scenario: 无效 model ID
- **WHEN** 请求体为 `{ model: "nonexistent/nonexistent-model" }`
- **THEN** 系统 SHALL 返回 400 错误 `"Invalid model: nonexistent/nonexistent-model"`

### Requirement: 已有 explore session 数据迁移

系统 SHALL 添加数据库 migration，将 explore_sessions 表中已有的裸 model ID **统一置为 NULL**。全限定格式的 ID 保持不变。

#### Scenario: 迁移裸 ID
- **WHEN** explore_sessions 中某条记录的 model 不包含 `/`（裸 ID）
- **THEN** 该记录的 model SHALL 置为 NULL
- **AND** 该 session 回退到全局默认模型

#### Scenario: 全限定 ID 不受影响
- **WHEN** explore_sessions 中某条记录的 model 已包含 `/`
- **THEN** 该记录的 model SHALL 不被修改
