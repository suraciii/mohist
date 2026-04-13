## Why

当前 mohist 的 Explore 功能只能使用全局配置的单一模型，用户无法根据讨论内容切换不同模型（如轻量级模型用于快速探索，强模型用于深度分析）。这限制了 explore 模式的灵活性，用户必须退出后手动修改配置文件才能换模型，体验割裂。

## What Changes

- **新增模型元数据系统**：为所有内置 provider（anthropic、openai、glm、kimi、minimax、deepseek、qwen 等）定义完整的模型列表，包括模型 ID、显示名、徽章（free/latest）、上下文窗口等元数据

- **扩展 Provider API**：增强 `/api/providers` 返回内置模型列表；新增 `/api/models` 端点聚合所有可用模型；新增 `/api/explore/:id/model` 端点支持切换会话模型

- **扩展 Explore 会话 Schema**：为 `explore_sessions` 表添加 `model` 和 `variant` 字段，支持 per-session 模型持久化

- **增强 Explore Agent**：支持从会话配置中读取模型覆盖全局默认配置

- **新增模型选择器 UI 组件**：在 Explore 页面 header 添加 `ModelSelectorPopover`，支持模糊搜索、provider 分组、最近使用、收藏等完整功能

- **升级 LLM 未配置提示**：优化 `LlmGuidanceCard`，引导用户连接 provider 后自动进入模型选择流程

## Capabilities

### New Capabilities

- `explore-model-selection`: Explore 页面模型选择功能，包括模型列表获取、搜索过滤、会话级模型切换
- `builtin-models-registry`: 内置模型元数据注册表，为所有支持的 provider 维护模型清单
- `per-session-model-persistence`: 会话级模型持久化，确保 reopen explore session 时恢复上次使用的模型

### Modified Capabilities

- `web-ui-provider-config`: Provider 配置页面需要展示可用模型列表（只读），帮助用户了解连接后能使用哪些模型

## Impact

- **前端**: 新增 `ModelSelector` 组件、`useModelSelection` hook、模型相关类型定义
- **API**: 修改 `/api/providers`，新增 `/api/models` 和 `/api/explore/:id/model`
- **数据库**: `explore_sessions` 表结构变更，需 migration
- **配置**: 可能需要扩展 config schema 支持 `agent.explore.model`（可选增强）
- **向后兼容**: 旧 explore session 无 model 字段时回退到全局配置，无破坏性变更
