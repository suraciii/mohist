## Context

当前 mohist 的 explore 功能（`packages/cli/src/agents/explore-agent.ts`）使用进程内 `streamText` 直接调用 Vercel AI SDK，模型通过 `resolveModel(config)` 解析，唯一来源是全局 `config.model`。Web UI 层面（`ExplorePage.tsx`）完全没有模型选择能力，用户只能手动修改 `~/.mohist/config.jsonc`。

参考 opencode 本体的实现，其模型选择器是一个复杂的 UI 组件（`dialog-select-model.tsx`），支持模糊搜索、provider 分组、最近使用、收藏等功能，并且每个会话可独立选择模型。

### 现有架构约束

1. **前端无 headless UI 库**：当前前端完全使用裸 React + Tailwind CSS，所有组件（Dialog、Dropdown）均为手写，无 `@headlessui/react`、Radix 等依赖。ModelSelector 需要引入 `@headlessui/react` 作为新依赖以满足 Popover + 键盘导航的可访问性需求。

2. **llmConfig 闭包捕获问题**：`createExploreRoutes()` 在 server 启动时接收 `llmConfig` 参数，之后所有请求共用同一个对象。要支持 per-session model，需要在每次请求时构造新的 config 对象（`{ ...load(), model: session.model ?? load().model }`），而非直接使用闭包中的 `llmConfig`。

3. **Migration 系统为单文件**：所有数据库迁移函数位于 `packages/cli/src/db/migrations.ts`（无 `migrations/` 目录），使用递增版本号模式（当前 `SCHEMA_VERSION = 9`），新迁移需添加 `migrateToVersion10()`。

4. **类型定义分散**：`ExploreSession` 接口定义在 `packages/cli/src/types/index.ts`，而 repo 映射在 `explore-session-repo.ts`，两者都需要同步更新。

## Goals / Non-Goals

**Goals:**

1. **内置模型注册表**：为所有内置 provider 维护完整的模型元数据，包括模型 ID、显示名、徽章（free/latest）、上下文窗口等
2. **API 增强**：提供统一的模型列表接口，聚合内置和自定义 provider 的可用模型
3. **会话级模型**：支持 per-session 模型选择，切换模型不影响其他会话或全局配置
4. **完整模型选择器 UI**：实现类 opencode 的模型选择器，支持搜索、分组、最近使用
5. **向后兼容**：旧会话无 model 字段时回退到全局配置

**Non-Goals:**

- 支持动态从 provider API 拉取模型列表（超出当前 scope，保持内置清单）
- 实现 model variant 的完整管理（Phase 2，首期只做简单 variant 选择）
- 修改 workflow agent（design/build/review）的模型选择逻辑
- 支持 `agent.explore.model` 配置覆盖（可选增强，非必须）

## Decisions

### Decision 1: 内置模型元数据放在后端还是前端？

**选择**：后端硬编码，API 返回完整模型列表。

**理由**：
- 单一数据源，前后端一致
- 后端 `resolveModel` 可以验证模型有效性
- 前端只负责渲染，逻辑更简单

**替代方案**：纯前端硬编码（被否决，前后端同步困难）

### Decision 2: 模型选择器放在哪里？用什么 UI 库？

**位置**：ExplorePage Header 右侧，类似 ChatGPT/Claude 网页版。

**UI 库**：新增 `@headlessui/react` 依赖，使用其 `Popover` / `Combobox` 组件。

**理由**：
- ModelSelector 需要 Popover + 搜索输入 + 键盘导航 + 列表交互，手写实现复杂度高且可访问性差
- 当前前端无任何 headless UI 库，这是引入的第一个
- `@headlessui/react` 与 Tailwind CSS 生态兼容，轻量无样式侵入

**替代方案**：继续手写（被否决，Popover + 搜索 + 键盘导航的可访问性难以保障）

### Decision 3: 会话级模型如何持久化？

**选择**：扩展 `explore_sessions` 表，添加 `model` 和 `variant` 字段。

**理由**：
- 最简单直接，与会话生命周期绑定
- 无需引入新的存储概念
- reopen session 时自动恢复

**数据流**：
```
用户选择模型 → POST /api/explore/:id/model → 更新 DB
                                    ↓
用户发送消息 → POST /:id/messages
                │
                ├── load() 清缓存后重新读取全局 config
                ├── 查询 session.model
                └── 构造 mergedConfig = { ...config, model: session.model ?? config.model }
                                    ↓
                          resolveModel(mergedConfig)
```

**关键实现细节**：`api/explore.ts` 中 `llmConfig` 是闭包捕获的，需要改为每次请求时重新 `load()` 并与 session model 合并。

### Decision 4: 如何处理内置 provider 的模型列表？

**选择**：新建 `builtin-models.ts`，为每个 provider 硬编码主流模型。

**理由**：
- 国产 provider（GLM、Kimi、MiniMax、DeepSeek、Qwen）通常没有标准 `/models` endpoint
- 硬编码确保体验一致性
- 主流模型相对稳定，维护成本可控

**首期支持模型清单**：
- **Anthropic**: claude-sonnet-4-20250514, claude-opus-4-20250514, claude-haiku-4-20250514
- **OpenAI**: gpt-4o, gpt-4o-mini, o3, o4-mini
- **GLM**: glm-4-flash, glm-4-plus, glm-4-air
- **Kimi**: kimi-k2.5, kimi-k2, kimi-k1.5
- **MiniMax**: minimax-text-01
- **DeepSeek**: deepseek-chat, deepseek-reasoner
- **Qwen**: qwen-max, qwen-plus, qwen-turbo
- **zhipuai-coding-plan**: 使用 zhipuai-coding-plan provider 的模型（如 glm-4-flash、glm-4-plus，通过 coding 专用 endpoint 代理）
- **kimi-for-coding**: 使用 kimi-for-coding provider 的模型（如 kimi-k2.5，通过 coding 专用 endpoint 代理）
- **minimax-for-coding**: 使用 minimax-for-coding provider 的模型（如 minimax-text-01，通过 coding 专用 endpoint 代理）

> 注：`*-for-coding` provider 本质上是代理层，共享对应基础 provider 的模型列表，只是使用不同的 baseURL 和 SDK（anthropic SDK）。

### Decision 5: 最近使用模型存储在哪里？

**选择**：前端 localStorage，键名 `mohist:recent-models`。

**理由**：
- 最近使用是用户个人偏好，无需服务端存储
- 简化实现，无需新增 API
- 与 opencode 做法一致

## Risks / Trade-offs

| 风险 | 缓解措施 |
|------|----------|
| 内置模型清单过时 | 建立更新机制：当用户报告模型不可用时，快速迭代发布 |
| 模型选择器 UI 复杂度高 | 引入 `@headlessui/react`，利用其 Popover/Combobox 原语降低实现难度 |
| per-session 模型导致成本不可控 | 在模型选择器显示模型价格提示（未来增强） |
| DB migration 失败 | 新字段可为 NULL，旧会话无感知回退 |
| 新增 `@headlessui/react` 依赖 | 该库轻量（~40KB gzip）、零样式、与 Tailwind 生态原生兼容 |
| config 缓存导致全局 model 变更不生效 | per-session model 存 DB 不受影响；全局 model 变更需 clearConfigCache + 重新 load |

### Decision 6: GET /api/models 与 GET /api/status 的关系

**选择**：两者并存，职责分离。

- `GET /api/status`：返回当前 LLM 是否配置成功 + 当前模型名（轻量，供 LlmGuidanceCard 判断）
- `GET /api/models`：返回完整可用模型列表（较重，供 ModelSelector 渲染）

**理由**：status 端点已在前端广泛使用，改动影响面大。新端点独立，职责清晰。

## Migration Plan

1. **数据库迁移**：在 `packages/cli/src/db/migrations.ts` 中添加 `migrateToVersion10()`，使用 `PRAGMA table_info` 防御性检查后 `ALTER TABLE explore_sessions ADD COLUMN model TEXT; ALTER TABLE explore_sessions ADD COLUMN variant TEXT;`，更新 `SCHEMA_VERSION` 为 10
2. **后端部署**：API 变更向后兼容，旧客户端仍可正常使用
3. **前端部署**：新 UI 组件渐进增强，无破坏性变更
4. **Rollback**：若发现问题，回滚前端即可，后端 API 变更无影响

## Open Questions

1. 是否需要支持 `agent.explore.model` 全局覆盖配置？（非必须，可增加灵活性）
2. variant 选择是做在 model selector 内还是单独控件？（建议首期做在 selector 内二级展开）
3. 是否需要显示模型的 context window 信息？（建议显示在 tooltip 中）
