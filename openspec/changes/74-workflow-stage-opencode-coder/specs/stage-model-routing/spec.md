## ADDED Requirements

### Requirement: stage 模型路由配置

系统 SHALL 支持在 `~/.mohist/config.jsonc` 中按 workflow stage 指定 opencode coder 模型。

#### Scenario: 配置全局默认模型
- **WHEN** 用户在 `config.jsonc` 中设置 `opencode.model`
- **THEN** 所有 stage 的 coder session 使用该模型，除非被 `stageModels` 覆盖

#### Scenario: 配置按 stage 覆盖模型
- **WHEN** 用户在 `config.jsonc` 中设置 `opencode.stageModels`
- **THEN** 当前 stage 对应的模型优先于 `opencode.model` 使用
- **AND** 支持的 stage 键为 `draft | plan | build | check | done`

#### Scenario: 模型选择优先级
- **WHEN** coder session 启动且 stage 为 X
- **THEN** 系统按以下顺序查找模型：1) `stageModels[X]` 2) `opencode.model` 3) opencode 内置默认
- **AND** 使用第一个找到的有效模型

### Requirement: stage 模型验证

系统 SHALL 在 spawn coder 前验证配置的模型是否在 discovered 可用模型列表中。

#### Scenario: 配置有效模型
- **WHEN** spawn coder 前检查发现配置的模型在可用列表中
- **THEN** 使用该模型启动 session，不报错

#### Scenario: 配置无效模型
- **WHEN** spawn coder 前检查发现配置的模型不在可用列表中
- **THEN** 拒绝启动，返回错误信息包含：配置值、可用模型列表
- **AND** 提示用户检查 config.jsonc

### Requirement: 模型失败自动回退

系统 SHALL 在配置的模型运行时不可用时自动回退到备用模型，并记录事件。

#### Scenario: 模型运行时失败
- **WHEN** 配置的模型在 session 过程中不可用（key 过期/额度用完/provider 故障）
- **THEN** 系统自动切换到 `opencode.model`（如果配置）或 opencode 内置默认
- **AND** 记录 `model_fallback` 事件到 workflow_log

### Requirement: 模型选择可见性

系统 SHALL 记录每次模型选择结果到 workflow_log。

#### Scenario: 记录模型选择
- **WHEN** coder session 成功选择模型并启动
- **THEN** workflow_log 记录 `model_selected` 事件，包含 `{ "model": "provider/model", "stage": "build", "source": "stageModels|opencode.model|default" }`

#### Scenario: 记录模型回退
- **WHEN** coder session 因模型不可用触发回退
- **THEN** workflow_log 记录 `model_fallback` 事件，包含 `{ "configured": "provider/model", "fallback": "provider/model", "stage": "build", "reason": "quota_exceeded|key_expired|provider_error" }`
