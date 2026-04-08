## Context

Mohist 的 agent-runtime 当前硬编码支持 anthropic 和 openai 两个 provider，API key 只能通过环境变量设置。用户在使用国产 LLM 供应商时无法配置，直接报错。当前配置全部存在 SQLite key-value 表中，没有文件配置系统。

opencode 项目的 provider 系统作为参考架构：多层配置合并（models.dev + 配置文件 + 环境变量 + auth.json）、20+ 内置 SDK、plugin 系统。但对 mohist 来说过度工程。mohist 只需要：文件配置、安全存 API key、支持多 provider。

关键约束：
- mohist 已依赖 `@ai-sdk/anthropic` 和 `@ai-sdk/openai`（Vercel AI SDK），不需要新增 AI SDK 依赖
- 国产供应商（GLM、Kimi、MiniMax、DeepSeek、Qwen）全部兼容 OpenAI API，通过 `createOpenAI({ baseURL })` 即可调用
- 配置文件放在 `~/.mohist/config.jsonc`，全局唯一，与 `~/.mohist/mohist.db` 并列
- mohist 是单用户开发工具，不需要 OAuth 或多用户认证

## Goals / Non-Goals

**Goals:**
- 用户通过编辑一个 JSONC 文件完成所有 LLM provider 配置
- 预定义常用供应商（国产 + 主流），用户只需填 apiKey
- 支持任意 openai-compatible 自定义 provider
- `mo providers` CLI 命令管理凭证
- API key 优先从配置文件读取，环境变量作为 fallback

**Non-Goals:**
- 不实现 OAuth 认证流程（opencode 的 plugin auth 系统）
- 不同步 models.dev 数据库
- 不实现项目级配置（只有全局 `~/.mohist/config.jsonc`）
- 不实现 TUI 模型选择器
- 不管理 opencode 的 provider 配置（用户自己配置 opencode）

## Decisions

### D1: 配置文件格式 — JSONC

**选择**: JSONC（JSON with Comments），与 opencode 一致。

**理由**: 用户可以在配置文件中写注释（解释哪个 key 是什么、为什么选这个 baseURL），纯 JSON 做不到。opencode 已验证这个格式在开发者工具中可行。

**实现**: 使用 `jsonc-parser`（VSCode 出品，MIT，~50KB）解析。不自己写正则 strip，因为 JSONC 的注释规则比想象中复杂（字符串内的 `//` 不是注释）。

### D2: API Key 存储 — 直接放 config.jsonc

**选择**: API key 直接写在 `config.jsonc` 的 `provider.<id>.apiKey` 字段。

**理由**: 
- mohist 是单用户个人工具，`~/.mohist/` 不会被 git 管理
- 减少概念数量：一个文件搞定，不需要 auth.json + config.json 两个文件
- opencode 也支持 `options.apiKey` 在配置文件中（opencode 是 auth.json 和 config 都支持）
- 未来如果需要分离，可以无缝迁移到独立 auth.json

**替代方案**: 分离 auth.json（仿 opencode）— 增加复杂度但安全性不增加（都是本地文件）。

### D3: Provider 解析 — 三类 SDK + 内置注册表

**选择**: 所有 provider 归为三类 SDK：

```
SDK类型              使用的构造函数                        适用 provider
─────────────────   ──────────────────                  ──────────────
"anthropic"         createAnthropic({ apiKey })          anthropic
"openai"            createOpenAI({ apiKey })             openai
"openai-compatible" createOpenAI({ apiKey, baseURL })    glm, kimi, minimax, deepseek, qwen, 自定义
```

内置注册表预定义每个 provider 的 SDK 类型和默认 baseURL：

```typescript
const BUILTIN_PROVIDERS = {
  anthropic: { sdk: "anthropic", name: "Anthropic" },
  openai:    { sdk: "openai",    name: "OpenAI" },
  glm:       { sdk: "openai-compatible", name: "智谱 GLM", baseURL: "https://open.bigmodel.cn/api/paas/v4" },
  kimi:      { sdk: "openai-compatible", name: "Kimi (Moonshot)", baseURL: "https://api.moonshot.cn/v1" },
  minimax:   { sdk: "openai-compatible", name: "MiniMax", baseURL: "https://api.minimax.chat/v1" },
  deepseek:  { sdk: "openai-compatible", name: "DeepSeek", baseURL: "https://api.deepseek.com" },
  qwen:      { sdk: "openai-compatible", name: "通义千问", baseURL: "https://dashscope.aliyuncs.com/compatible-mode/v1" },
};
```

用户可以覆盖内置 baseURL，也可以定义全新的 provider（自动识别为 openai-compatible）。

**替代方案**: 每个 provider 一个独立 SDK（仿 opencode 的 20+ bundled SDK）— 国产供应商都有 OpenAI 兼容 API，没必要引入额外依赖。

### D4: 配置加载流程 — ConfigLoader 服务

**选择**: 新增 `ConfigLoader` 类，Server 启动时初始化，全生命周期复用。

```
~/.mohist/config.jsonc 存在？
  ├─ 是 → 解析 JSONC → Zod 校验 → 提取 provider + model 配置
  └─ 否 → 使用内置默认值（无 provider 配置，仅支持环境变量）

对每个 providerID:
  1. 查内置注册表 → 得到 SDK 类型 + 默认 baseURL
  2. 查配置文件 provider.<id> → 覆盖 baseURL, 补充 apiKey
  3. 查环境变量 (PROVIDER_ENV 映射) → fallback apiKey
  4. 合并结果
```

**替代方案**: 继续从 SQLite 读 `llm.*` 键 — 不符合"文件配置"目标，且 SQLite 不适合存结构化嵌套数据。

### D5: 与现有 SQLite 配置的关系

**选择**: config.jsonc 中也支持 `server` 和 `agent` 配置段，但 SQLite 中的 `server.port`、`agent.timeout` 等继续生效。config.jsonc 优先于 SQLite。

**理由**: 渐进迁移，不一次性破坏现有配置。SQLite 中 `llm.*` 键不再使用（因为 provider 配置迁移到文件），但其他键继续工作。

### D6: `mo providers login` 交互方式

**选择**: 简单的命令行 readline 交互。

```
$ mo providers login glm
? Enter API Key for 智谱 GLM: ****
✓ Saved to ~/.mohist/config.jsonc
```

直接修改 config.jsonc 文件中的 `provider.glm.apiKey` 字段。如果 provider 段不存在则创建。

**替代方案**: 使用 `@clack/prompts`（opencode 用的）— 引入 TUI 依赖，过度工程。

## Risks / Trade-offs

- **[API key 明文存储]** → `~/.mohist/` 目录权限应为 0700，config.jsonc 文件权限 0600。与 SSH 私钥存储方式一致。实际风险低（本地单用户）。
- **[JSONC 解析引入依赖]** → `jsonc-parser` 是 VSCode 团队维护的成熟库，MIT 协议，~50KB，无子依赖。风险极低。
- **[内置 provider 注册表需要维护]** → 国产供应商的 baseURL 可能变化。但注册表很小（7 个），且用户可以覆盖。风险低。
- **[SQLite 到文件配置的迁移]** → 用户可能不知道 `llm.*` 键已迁移。Server 启动时检测 SQLite 中是否有 `llm.model` 键，如果有则打印提示。不自动迁移，避免意外。

## Open Questions

- `mo providers login` 是否需要支持从环境变量自动导入？（例如检测到 `ANTHROPIC_API_KEY` 已设置，提示用户是否保存到配置文件）
- 是否需要 `mo config edit` 命令直接打开编辑器编辑 config.jsonc？（便利性功能，可后续添加）
