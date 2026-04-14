## Context

当前 mohist 模型系统由三个静态文件驱动：`builtin-providers.ts`（provider SDK/baseURL 定义）、`builtin-models.ts`（硬编码模型列表）、`config-loader.ts`（配置合并）。参考项目 opencode 使用 [models.dev](https://models.dev) 作为模型元数据来源，构建时内嵌 snapshot + 运行时定期刷新，实现模型列表的自动更新。

当前缺陷链路：

```
用户配置了 provider 的 apiKey
    → /providers/models API 返回裸 ID "minimax-text-01"
    → 前端选择后存入 explore_sessions.model = "minimax-text-01"
    → resolveModel() 期望 "provider/model-id" 格式 → 爆炸
```

同时 `resolveModel()` 默认模型硬编码为 `anthropic/claude-sonnet-4-20250514`，未配 anthropic 的用户直接无法使用。

## Goals / Non-Goals

**Goals:**
- 引入 models.dev 作为模型元数据来源，消除硬编码过时问题
- 全链路统一 model ID 为 `provider/model-id` 格式
- 智能默认模型选择（选第一个已配置 provider 的最新模型）
- 修复 explore session 的 model ID 存储/解析
- 保持向后兼容：已配置的 config.jsonc 仍能正常工作

**Non-Goals:**
- 不做 models.dev 的完整 provider SDK 动态加载（如 opencode 那样自动 import npm 包）
- 不修改 agent-runner-service 的 spawn opencode 子进程逻辑
- 不做前端 UI 重构，仅修数据格式
- 不支持 models.dev 不包含的私有/自部署 provider 的模型自动发现

## Decisions

### Decision 1: models.dev 集成方式 — 构建 snapshot + 运行时缓存

**选择**: 参考 opencode 的 `ModelsDev` namespace 模式，构建时从 `https://models.dev/api.json` 生成 snapshot 文件，运行时从本地缓存读取 + 后台定期刷新。

**替代方案**:
- A) 纯硬编码更新 → 拒绝：每次新模型都需要改代码发版
- B) 纯运行时 fetch → 拒绝：首次启动、离线环境无模型数据
- C) 只用构建 snapshot → 拒绝：snapshot 随时间过时

**实现**:
```
构建时 (npm run build):
  fetch("https://models.dev/api.json")
  → 生成 src/config/models-snapshot.ts
  → const snapshot: Record<string, ModelsDevProvider>
  → 网络失败时：若已有 models-snapshot.ts 则复用；
    若不存在可通过 MODELS_DEV_API_JSON 指定本地 JSON 生成

运行时 (src/config/models-dev.ts):
  ModelsDev.get()
    → 1. 读内存缓存
    → 2. 读 ~/.mohist/cache/models.json (TTL 1h)
    → 3. 回退到构建时内嵌 snapshot
    → 4. 后台刷新 (setInterval 1h)
```

数据结构映射：
```typescript
// models.dev 的 provider 条目
interface ModelsDevProvider {
  id: string;          // "minimax"
  name: string;        // "MiniMax (minimax.io)"
  npm: string;         // "@ai-sdk/anthropic"
  api?: string;        // "https://api.minimax.io/anthropic/v1"
  env: string[];       // ["MINIMAX_API_KEY"]
  models: Record<string, ModelsDevModel>;
}

// 映射到 mohist 内部
npm → sdk 类型:
  "@ai-sdk/anthropic"    → "anthropic"
  "@ai-sdk/openai"       → "openai"
  其他                   → "openai-compatible"
```

### Decision 2: model ID 全链路统一为 models.dev 的 `provider/model-id`

**选择**: 所有位置统一使用全限定 ID，且 **直接使用 models.dev 中的 provider ID**。

**变更点**:
- `/providers/models` API: model `id` 字段改为 `"minimax/MiniMax-M2.7"`
- `POST /explore/:id/model`: 接收全限定 ID
- explore_sessions 表: 存储 `"minimax/MiniMax-M2.7"` 而非裸 ID
- `resolveModel()`: 不变，本来就期望 `provider/model-id`
- config.jsonc `model` 字段: 改为 models.dev 对应的全限定格式（如 `minimax/MiniMax-M2.7`）
- **provider ID 直接对齐 models.dev**: 使用 `minimax`（替换原 `minimax-for-coding`）、`moonshotai`（替换原 `kimi`）、`zhipuai`（替换原 `glm`）、`alibaba`（替换原 `qwen`）等

**前端影响**: `api.ts` 的 `updateSessionModel()` 发送的 model 值需要是全限定格式。前端 model 选择器从 `/providers/models` 获取的 `id` 已经是全限定格式，直接传回即可。

### Decision 3: 智能默认模型选择

**选择**: 移除 `DEFAULT_MODEL` 常量，改为运行时从已配置 provider 中自动选择。

**算法**:
```
resolveDefaultModel(config):
  1. 如果 config.model 有值 → 直接用
  2. 收集所有有 apiKey 的 provider
  3. 从 models.dev 数据中，为每个有 key 的 provider 找最新模型
     (按 release_date 降序排列)
  4. 返回第一个 "provider/latest-model-id"
  5. 如果没有已配置 provider → 抛出友好错误
     "No LLM provider configured. Run 'mo providers add' or set an API key."
```

**注意**: provider ID 与 models.dev 完全对齐，如用户在 config.jsonc 中配置了 `provider.minimax.apiKey`，则自动从 models.dev 的 `minimax` provider 中选择最新模型。

### Decision 4: provider 注册表从 models.dev 生成

**选择**: `BUILTIN_PROVIDERS` 不再手动维护，改为从 models.dev 数据提取，且 **provider ID 直接使用 models.dev 的 ID**。

**models.dev 中的 provider ID 直接作为 mohist 的 provider ID 使用**，不再维护任何别名或映射层。主要变更如下（仅用于说明，系统内不存在映射表）：
- `minimax` — 来自 models.dev，取代原硬编码的 `minimax-for-coding`
- `moonshotai` — 来自 models.dev，取代原硬编码的 `kimi`
- `zhipuai` — 来自 models.dev，取代原硬编码的 `glm`
- `alibaba` — 来自 models.dev，取代原硬编码的 `qwen`
- `zhipuai-coding-plan`、`kimi-for-coding` — models.dev 中已存在，直接使用

对于 models.dev 中没有的 provider（如有需要可保留少量静态 fallback），保留静态 fallback 定义。

### Decision 5: 数据迁移 — explore_sessions 裸 model ID

**选择**: 添加 DB migration，将 explore_sessions 中已有的裸 model ID **统一置空**。

**策略**: 由于系统统一迁移到 models.dev 的全限定 ID 格式，且不再维护旧模型映射，对于已有裸 model ID 的记录直接将其 `model` 字段设为 `NULL`，使 session 回退到全局默认模型选择逻辑。全限定格式的 ID 保持不变。

## Risks / Trade-offs

- **[models.dev 不可用]** → 有构建时 snapshot 兜底，不影响已发布版本。运行时刷新失败静默跳过，继续用缓存/snapshot。
- **[API 响应格式变更]** → `/providers/models` 返回的 model ID 从裸 ID 变为全限定 ID，是 **BREAKING** 变更。前端需同步更新。由于前端和后端同仓部署，不存在版本不一致问题。
- **[models.dev 数据错误]** → npm/sdk 映射可能不适用于 mohist 场景。通过 fallback 到静态 BUILTIN_PROVIDERS 缓解。
- **[DB migration 数据丢失]** → 裸 ID 记录统一置空后，旧 session 会回退到全局默认模型，用户可能需要重新选择模型。由于不再维护旧模型映射，这是可接受的。
