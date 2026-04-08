## Why

Mohist 的 agent-runtime 只支持 anthropic 和 openai 两个 provider，且 API key 只能通过环境变量设置。用户在使用国产 LLM 供应商（GLM、Kimi、MiniMax、DeepSeek、Qwen）时没有配置入口，直接报错 "API key not found"。需要一套仿照 opencode 的 provider 配置系统，让用户通过配置文件管理多个 LLM 供应商及其凭证。

## What Changes

- 新增全局配置文件 `~/.mohist/config.jsonc`，支持 JSONC 格式（带注释的 JSON），包含 provider 和 model 配置
- 新增内置 provider 注册表，预定义 anthropic、openai、glm、kimi、minimax、deepseek、qwen 等供应商的 SDK 类型和默认 baseURL
- 重写 `resolveModel()` 以支持动态 provider 解析：anthropic SDK、openai SDK、openai-compatible SDK（用于所有 OpenAI 兼容供应商）
- 新增 `mo providers` CLI 命令组：`list`、`login`、`logout`
- 新增配置加载器（ConfigLoader），从文件 + 环境变量合并 provider 配置
- **BREAKING** SQLite 中的 `llm.model`、`llm.provider.*` 配置键迁移到 config.jsonc
- Server 启动时从 config.jsonc 加载 provider 配置（而非 SQLite ConfigRepo）

## Capabilities

### New Capabilities
- `provider-config`: 全局配置文件系统——config.jsonc 的加载、解析、校验、合并，以及内置 provider 注册表
- `provider-registry`: 动态 provider 解析——根据配置创建对应的 AI SDK 实例（anthropic / openai / openai-compatible），替代硬编码 switch-case
- `provider-cli`: `mo providers` 命令组——list/login/logout 子命令，管理 provider 凭证和查看状态

### Modified Capabilities
- `agent-runtime`: LLM provider 配置来源从 SQLite ConfigRepo 改为 ConfigLoader（config.jsonc + 环境变量），resolveModel() 支持动态 provider
- `server-daemon`: Server 启动时初始化 ConfigLoader，将 provider 配置传递给 agent-runtime（不再从 SQLite 读 llm.* 键）
- `cli-interface`: 新增 `mo providers` 命令组及其子命令

## Impact

- **新增文件**: `packages/cli/src/config/` 目录（config-loader.ts, config-schema.ts, builtin-providers.ts），`packages/cli/src/cli/commands/providers.ts`
- **重写文件**: `packages/cli/src/agent-runtime/llm.ts`（从硬编码 switch-case 改为注册表查询）
- **修改文件**: `packages/cli/src/server/index.ts`（buildLlmConfig 改为 ConfigLoader），`packages/cli/src/cli/index.ts`（注册新命令）
- **新增依赖**: `jsonc-parser`（VSCode 出品，用于解析 JSONC），或使用正则 strip comments 的轻量方案
- **数据迁移**: SQLite 中的 `llm.model`、`llm.provider.*` 键需要迁移引导（或并存期后移除）
- **用户数据**: 新增 `~/.mohist/config.jsonc` 文件（全局配置）
