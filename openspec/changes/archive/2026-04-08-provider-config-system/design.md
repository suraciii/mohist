## Context

Mohist 的 agent-runtime 当前硬编码支持 anthropic 和 openai，API key 只能通过环境变量设置。用户无法使用国产 LLM。

经过深入分析 opencode 的 provider 系统，我们发现：
1. **models.dev** 已经维护了完整的模型元数据（provider、baseURL、SDK 类型等）
2. **所有国产模型**都支持 OpenAI 兼容 API，可以用 `@ai-sdk/openai-compatible` 统一调用
3. **Issue Provider** 是设计过度的僵尸代码，从未实例化

## Goals / Non-Goals

**Goals:**
- 用户通过极简配置（仅 apiKey）使用多个 LLM provider
- 支持国产模型：智谱 GLM、Moonshot Kimi、MiniMax
- 自动从 models.dev 获取最新的模型列表和元数据
- 清理 Issue Provider 僵尸代码

**Non-Goals:**
- 不实现 OAuth 认证（mohist 是单用户工具）
- 不维护独立的模型数据库（复用 models.dev）
- 不实现项目级配置（只有全局配置）
- 不实现 TUI 模型选择器
- 不保留 Issue Provider（GitHub 集成后续重新设计）

## Decisions

### D1: 配置文件 — 极简 JSON

**选择**: `~/.mohist/config.json`，纯 JSON，仅包含 apiKey 配置。

```json
{
  "model": "zhipu/glm-4-flash",
  "providers": {
    "anthropic": { "apiKey": "${env:ANTHROPIC_API_KEY}" },
    "zhipu": { "apiKey": "sk-xxx" },
    "moonshot": { "apiKey": "${env:MOONSHOT_API_KEY}" }
  }
}
```

**理由**:
- models.dev 已经提供完整的 provider 元数据（baseURL、名称等）
- 本地只需配置 apiKey，极简
- 支持 `${env:VAR}` 语法从环境变量读取
- 与 opencode 配置格式兼容

**替代方案**: JSONC（带注释）— 不必要，配置足够简单。

### D2: 模型数据来源 — models.dev

**选择**: 直接从 https://models.dev/api.json 获取。

**缓存策略**:
- **首次启动**: 自动拉取，缓存到 `~/.mohist/models-cache.json`
- **TTL**: 5 分钟（参考 opencode）
- **自动刷新**: 启动时检查，每小时后台检查
- **强制刷新**: `mo models sync` 命令
- **文件锁**: 防止并发写入（参考 opencode 的 `Flock`）

**数据格式**:
```json
{
  "zhipu": {
    "id": "zhipu",
    "name": "智谱 AI",
    "env": ["ZHIPU_API_KEY"],
    "npm": "@ai-sdk/openai-compatible",
    "api": "https://open.bigmodel.cn/api/paas/v4",
    "models": { "glm-4": {...}, "glm-4-flash": {...} }
  }
}
```

### D3: Provider 解析 — 三类 SDK

所有 provider 归为三类：

| SDK 类型 | 构造函数 | 适用 provider |
|---------|---------|--------------|
| `anthropic` | `createAnthropic({ apiKey })` | anthropic |
| `openai` | `createOpenAI({ apiKey })` | openai |
| `openai-compatible` | `createOpenAI({ apiKey, baseURL })` | zhipu, moonshot, minimax, 自定义 |

通过 `model.api.npm` 字段判断：
- `"@ai-sdk/anthropic"` → anthropic SDK
- `"@ai-sdk/openai"` → openai SDK
- `"@ai-sdk/openai-compatible"` → openai-compatible SDK（所有国产模型）

### D4: apiKey 优先级

1. **用户配置** `config.json` 中的 `provider.<id>.apiKey`
2. **环境变量** models.dev 定义的 `provider.env` 数组
3. **运行时**: 缺失 apiKey 的 provider 会被跳过（不显示其模型）

### D5: Issue Provider 清理

**现状**: `src/providers/interface.ts`、`local.ts`、`index.ts` 定义了 IssueProvider 接口，但：
- 从未被实例化
- 从未被引用
- GitHub 集成需要重新设计

**决策**: 直接删除整个 `src/providers/` 目录。

### D6: 配置加载流程

```
Server 启动
  ├─ 1. 加载 ~/.mohist/config.json
  │     └─ 解析 ${env:VAR} 语法
  ├─ 2. 检查 models-cache.json
  │     ├─ 存在且未过期 → 使用缓存
  │     └─ 不存在或过期 → 从 models.dev 拉取
  ├─ 3. 合并配置
  │     ├─ 模型数据: models-cache
  │     └─ apiKey: config.json
  └─ 4. 初始化 ProviderRegistry
```

### D7: `mo models` CLI 命令

```bash
# 列出所有可用模型（带 provider 分组）
$ mo models list

# 强制刷新 models.dev 缓存
$ mo models sync
```

## Risks / Trade-offs

- **[网络依赖]** → models.dev 需要联网，但 5min 缓存保证短时间离线可用。若完全离线，可手动编辑 models-cache.json。
- **[models.dev 可用性]** → 参考 opencode 的实现，可用性高。极端情况下可内置基础配置作为 fallback。
- **[API key 明文存储]** → `~/.mohist/` 目录权限 0700，config.json 权限 0600。与 SSH 私钥存储方式一致。

## Implementation Notes

### models.dev 相关字段

从 models.dev 获取的关键字段：
- `provider.id` — provider 标识符（zhipu, moonshot 等）
- `provider.name` — 显示名称
- `provider.npm` — SDK 包名（判断使用哪个 SDK）
- `provider.api` — baseURL
- `provider.models` — 模型列表，包含：
  - `id` — 模型 ID
  - `name` — 模型显示名称
  - `tool_call` — 是否支持工具调用
  - `limit.context` — 上下文窗口
  - `limit.output` — 最大输出长度

### 国产模型配置示例

**智谱 GLM (zhipu)**:
- npm: `@ai-sdk/openai-compatible`
- api: `https://open.bigmodel.cn/api/paas/v4`
- models: `glm-4`, `glm-4-flash`, `glm-4v`

**Moonshot Kimi (moonshot)**:
- npm: `@ai-sdk/openai-compatible`
- api: `https://api.moonshot.cn/v1`
- models: `kimi-k2`, `kimi-k2-thinking`

**MiniMax (minimax)**:
- npm: `@ai-sdk/openai-compatible`
- api: `https://api.minimax.chat/v1`
- models: `abab7-chat`

## References

- opencode models.ts: `opensrc/opencode/packages/opencode/src/provider/models.ts`
- opencode provider.ts: `opensrc/opencode/packages/opencode/src/provider/provider.ts` (Lines 127-150: BUNDLED_PROVIDERS, Lines 1333-1468: resolveSDK)
