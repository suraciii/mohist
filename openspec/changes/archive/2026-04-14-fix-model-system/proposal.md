## Why

模型系统存在三个相互关联的缺陷：(1) 默认模型硬编码为 `anthropic/claude-sonnet-4-20250514`，未配置 anthropic key 的用户无法使用；(2) 前端选择模型后传递裸 model ID（如 `minimax-text-01`）给后端，缺少 provider 前缀，导致 `resolveModel()` 解析失败；(3) 内置模型列表硬编码在 `builtin-models.ts` 中，严重滞后于供应商实际可用模型（如 MiniMax 已有 M2.7 但列表中只有 `minimax-text-01`）。

## What Changes

- **引入 models.dev 作为模型元数据来源**：构建时生成 snapshot，运行时定期刷新缓存，彻底替代硬编码的 `builtin-models.ts` 和 `BUILTIN_PROVIDERS`
- **统一 model ID 为全限定格式 `provider/model-id`**：API 响应、前端选择、DB 存储、`resolveModel()` 解析全部使用 `provider/model-id` 格式，消除裸 ID 歧义
- **智能默认模型选择**：移除硬编码默认模型，改为自动选择第一个已配置（有 apiKey）的 provider 的最新模型
- **修复 explore session model 存储**：`POST /explore/:id/model` 接收并存储全限定 model ID，确保 `resolveModel()` 能正确解析
- **更新内置 provider 注册表**：从 models.dev 数据同步 provider 的 SDK 类型、baseURL、环境变量映射，provider ID 直接使用 models.dev ID（如 `minimax`、`moonshotai`、`zhipuai`、`alibaba`）

## Capabilities

### New Capabilities
- `models-dev-integration`: models.dev 远程模型元数据获取、缓存、snapshot 生成，替代硬编码模型列表
- `smart-default-model`: 根据已配置 provider 自动选择默认模型，替代硬编码 anthropic 默认值

### Modified Capabilities
- `provider-config`: 更新内置 provider 注册表，从 models.dev 同步 SDK/npm/baseURL 映射；增加 `models` 字段覆盖支持
- `provider-registry`: 统一 model ID 为全限定格式；修复 explore session model ID 存储；修复 `/providers/models` API 响应格式
- `agent-runtime`: 默认模型选择逻辑改为智能选择；model ID 验证适配全限定格式

## Impact

- **核心文件变更**：
  - `src/config/builtin-models.ts` → 替换为 models.dev 数据驱动
  - `src/config/builtin-providers.ts` → 从 models.dev 数据生成
  - `src/agent-runtime/llm.ts` → 默认模型选择逻辑重写
  - `src/api/providers.ts` → `/models` API 返回全限定 model ID
  - `src/api/explore.ts` → model ID 验证和存储修复
  - `src/agents/explore-agent.ts` → model 解析适配
- **新增依赖**：需要 models.dev API 的 fetch + 缓存机制（参考 opencode 的 `ModelsDev` namespace）
- **构建流程**：build 脚本需要增加 models.dev snapshot 生成步骤
- **API 变更**：`/providers/models` 响应中的 model `id` 字段从裸 ID 变为 `provider/model-id`
- **数据库兼容**：explore_sessions 表中已有的裸 model ID 统一置空，回退到全局默认模型
