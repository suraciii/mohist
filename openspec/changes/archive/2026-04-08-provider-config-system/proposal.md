## Why

Mohist 的 agent-runtime 只支持 anthropic 和 openai 两个 provider，且 API key 只能通过环境变量设置。用户在使用国产 LLM 供应商（GLM、Kimi、MiniMax）时没有配置入口，直接报错 "API key not found"。

参考 opencode 的 provider 系统架构后，我们决定采用更简洁的方案：
1. **直接使用 models.dev** - 作为单一事实源获取模型元数据（名称、baseURL、SDK 类型）
2. **本地仅配置 apiKey** - 极简配置，用户只需在 `~/.mohist/config.json` 中配置 apiKey
3. **支持国产模型** - 通过 `@ai-sdk/openai-compatible` 统一支持所有 OpenAI 兼容 API

同时需要清理现有 Issue Provider 的僵尸代码。

## What Changes

- **清理**: 删除 `src/providers/` 目录（IssueProvider 僵尸代码，从未使用）
- **配置**: 新增 `~/.mohist/config.json` 配置文件，仅包含 provider apiKey 配置
- **模型数据**: 从 models.dev (https://models.dev/api.json) 获取 provider 和模型元数据
- **缓存**: 自动缓存 models.dev 数据到 `~/.mohist/models-cache.json`（TTL=5min）
- **SDK**: 重写 `resolveModel()` 支持动态 provider 解析
  - anthropic → `@ai-sdk/anthropic`
  - openai → `@ai-sdk/openai`  
  - 国产模型 → `@ai-sdk/openai-compatible`
- **CLI**: 新增 `mo models` 命令组：`list`、`sync`
- **数据迁移**: 弃用 SQLite 中的 `llm.*` 配置键

## Capabilities

### New Capabilities
- `provider-config`: 全局配置文件系统——config.json 的加载、解析、校验
- `provider-registry`: 动态 provider 解析——根据 models.dev 数据创建对应的 AI SDK 实例
- `provider-cli`: `mo models` 命令组——list（列出可用模型）、sync（强制刷新缓存）
- `models-cache`: models.dev 数据缓存——自动同步、TTL 管理、文件锁

### Modified Capabilities
- `agent-runtime`: LLM provider 配置来源从 SQLite ConfigRepo 改为 config.json + models.dev 缓存
- `server-daemon`: Server 启动时加载 config.json 并同步 models.dev 数据

### Removed
- `issue-provider`: 删除 IssueProvider 接口和相关代码（设计过度，从未使用）

## Impact

- **删除文件**: `packages/cli/src/providers/` 整个目录
- **新增文件**: 
  - `packages/cli/src/config/config-loader.ts`（配置加载器）
  - `packages/cli/src/config/models-cache.ts`（models.dev 缓存管理）
  - `packages/cli/src/cli/commands/models.ts`（CLI 命令）
- **重写文件**: `packages/cli/src/agent-runtime/llm.ts`（动态 provider 解析）
- **修改文件**: 
  - `packages/cli/src/server/index.ts`（初始化配置加载器）
  - `packages/cli/src/cli/index.ts`（注册新命令）
- **新增依赖**: 无（使用内置 `fetch` 和文件操作）
- **用户数据**: 新增 `~/.mohist/config.json` 和 `~/.mohist/models-cache.json`

## References

- opencode models.dev: `opensrc/opencode/packages/opencode/src/provider/models.ts`
- opencode provider resolution: `opensrc/opencode/packages/opencode/src/provider/provider.ts`
