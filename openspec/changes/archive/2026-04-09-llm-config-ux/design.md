## Context

mohist 的 LLM 配置系统在之前的工作中已经建立了 provider 注册表、config.jsonc 加载、环境变量 fallback 等基础设施。但用户侧的体验存在断裂：系统静默 fallback 到 `anthropic/claude-sonnet-4-20250514`，直到运行时才在错误中暴露配置缺失。前端只能展示原始错误信息，无法引导用户。

当前架构：
- `config-loader.ts` 加载 `~/.mohist/config.jsonc`，无文件时返回空 `{}`
- `llm.ts` 的 `resolveModel()` 同步解析 model，找不到 key 时抛出 `Error`
- `api/explore.ts` 在 catch 中返回 `{ error: message }` + HTTP 500
- 前端 `useExploreStream` 只展示 `error.message` 原文

## Goals / Non-Goals

**Goals:**
- 前端在用户进入 LLM 依赖功能前就能感知 LLM 是否就绪
- LLM 相关错误被分类为结构化错误码，前端按类型展示不同引导
- 不阻塞服务器启动，不改变现有配置加载流程

**Non-Goals:**
- 不做启动时强制校验 LLM 配置（用户可能先启动再配置）
- 不做 LLM 连通性探测（不发真实请求验证 key 有效性）
- 不修改 `resolveModel()` 的核心逻辑
- 不做配置引导向导 UI

## Decisions

### Decision 1: 在 `/api/status` 暴露 LLM 状态

在现有 `/api/status` 端点增加 `llm` 字段，通过 try/catch 调用 `resolveModel()` 判断是否可用。

**为什么不用单独端点**: `/api/status` 已存在且前端会请求，不增加额外网络开销。LLM 状态本质是系统状态的一部分。

**字段设计**:
```json
{
  "llm": {
    "configured": true,
    "provider": "glm",
    "model": "glm-4-plus"
  }
}
```
不暴露 apiKey 等敏感信息。`configured` 为 false 时只返回 `{ configured: false }`。

### Decision 2: 自定义错误类型承载错误码

创建 `LlmError` 类继承 `Error`，携带 `code` 字段。在 `resolveModel()` 的现有抛错点改用 `LlmError`。

错误码映射：
- `LLM_NOT_CONFIGURED` — apiKey 不存在或 config.jsonc 不存在
- `LLM_CONFIG_INVALID` — config.jsonc 存在但 JSONC 语法错误
- `LLM_AUTH_FAILED` — provider 返回 401/403（future，当前不改 stream 层）
- `LLM_RATE_LIMITED` — provider 返回 429（future）
- `LLM_PROVIDER_ERROR` — provider 返回 5xx（future）

当前只实现 `LLM_NOT_CONFIGURED` 和 `LLM_CONFIG_INVALID`，其余为 API 层 catch 中的预留分类。

**为什么不用 error message 解析**: 字符串解析脆弱，自定义类型可靠且可扩展。

### Decision 3: API 错误响应增加 code 字段

在 `api/explore.ts` 和 `api/issues.ts`（start 端点）的 catch 中，检测 `LlmError` 并在响应中附加 `code` 字段：

```json
{
  "success": false,
  "error": "API key not found for provider \"anthropic\"...",
  "code": "LLM_NOT_CONFIGURED"
}
```

前端根据 `code` 展示不同 UI。无 `code` 时保持原有错误展示。

### Decision 4: Explore 页面 LLM 状态检查

前端在 `ExplorePage` 加载时查询 status API 的 `llm` 字段。如果 `configured === false`，渲染引导卡片替代聊天界面。

引导卡片展示：配置文件路径、支持的 provider 列表（anthropic、glm、openai）、配置示例（含 anthropic 和 glm 的 JSONC 示例）、刷新按钮。

### Decision 5: 刷新按钮机制

引导卡片包含刷新按钮，点击后重新查询 status API。这是为了让用户在手动修改配置文件后能即时看到效果，无需刷新整个页面。

**实现方式**: 前端利用 react-query 的 `refetch` 功能，刷新按钮触发 status API 重新请求。如果返回 `configured: true`，则切换到正常聊天界面。

## Risks / Trade-offs

- **[resolveModel 被多调用一次]** → status 端点每次请求都调用 resolveModel，对性能无影响（纯内存操作，无网络请求）
- **[status 端点返回的 provider/model 信息可能过时]** → 用户运行时修改 config.jsonc 后需要点击刷新按钮才能更新状态，这是可接受的 trade-off
- **[只覆盖 Explore 页面]** → Issues 的 start 端点也会受影响，但 agent 错误通过 SSE/eventBus 传播，当前只做 API 层 code 字段，前端引导留给后续
- **[配置热重载的考虑]** → 如果 mohist 后续支持 config 热重载（不重启 server），需要确认 `resolveModel()` 是纯函数还是有副作用。当前实现假设 `resolveModel()` 每次调用都会读取最新配置，如有缓存逻辑需同步调整
- **[并发场景]** → 多次快速请求 status 端点会并发调用 `resolveModel()`，需确保无副作用或竞态条件
