## Context

mohist 的 provider 系统基于 `builtin-providers.ts` 静态注册表 + `config-loader.ts` 合并逻辑 + `llm.ts` SDK 工厂。当前已注册 7 个 provider（anthropic、openai、glm、kimi、minimax、deepseek、qwen），全部使用 `openai-compatible` 或原生 SDK。

opencode 的实现通过 models.dev 外部数据源（snapshot.js）管理 provider，其中 coding plan provider 与普通 provider 是独立的注册条目，使用不同的 API 端点和 SDK 类型：

```
普通 provider          →  coding plan provider
─────────────────     ──────────────────────────────────────
glm    → openai-compat → zhipuai-coding-plan → openai-compat  (不同端点)
kimi   → openai-compat → kimi-for-coding     → anthropic      (不同协议！)
minimax→ openai-compat → minimax-for-coding  → anthropic      (不同协议！)
```

## Goals / Non-Goals

**Goals:**
- 支持 3 家 coding plan provider，用户配置 `model: "kimi-for-coding/k2p5"` 即可使用
- 复用现有 `llm.ts` 的 SDK 工厂（已支持 `anthropic` SDK 类型）
- CLI `providers list` 命令能显示 coding plan provider

**Non-Goals:**
- 不实现 interleaved/reasoning 字段映射（当前 mohist 不需要 thinking 模式）
- 不实现 models.dev 风格的动态 provider 发现
- 不修改 config-schema.ts（`sdk` 枚举已包含 `anthropic`）

## Decisions

### 1. Coding plan provider 作为独立 builtin 条目

与 opencode 保持一致，coding plan provider 使用独立的 providerID（如 `kimi-for-coding` 而非 `kimi`），原因：
- API 端点完全不同（`api.kimi.com/coding/v1` vs `api.moonshot.cn/v1`）
- SDK 类型可能不同（`anthropic` vs `openai-compatible`）
- 用户可能在同一项目中同时使用普通 API 和 coding plan

### 2. Kimi/MiniMax coding plan 使用 anthropic SDK

opencode 的 models.dev 数据明确标注：
- `kimi-for-coding` → `npm: "@ai-sdk/anthropic"`
- `minimax` → `npm: "@ai-sdk/anthropic"` + `api: "https://api.minimax.io/anthropic/v1"`

这意味着这两家的 coding plan 端点兼容 Anthropic Messages API 格式。mohist 的 `llm.ts` 已支持 `anthropic` SDK 类型，无需改动。

### 3. 不新增 SDK 类型

`config-schema.ts` 的 `SdkType` 已包含 `'anthropic'`，不需要扩展。

## Risks / Trade-offs

- [Coding plan 端点可能变更] → 使用配置文件 `baseURL` 覆盖机制应对
- [Anthropic SDK 兼容性不完全] → 依赖 opencode 社区验证，这些端点已在 opencode 中被广泛使用
- [API key 共用] → `kimi-for-coding` 和 `kimi` 共用 `KIMI_API_KEY` 环境变量，这是预期行为（订阅绑定同一账号）
