## Why

用户首次使用 mohist 时，`~/.mohist/config.jsonc` 不存在，系统静默 fallback 到 `anthropic/claude-sonnet-4-20250514`。当用户在 Explore 页面发送消息时，`resolveModel()` 因找不到 API key 而抛出错误，前端通过 SSE stream error 显示技术性错误信息。用户无法从 UI 中理解问题根因，也不知道如何配置 LLM provider。

## What Changes

- `/api/status` 端点增加 `llm` 字段，暴露当前 LLM 配置状态（configured、provider、model），前端可在进入 LLM 依赖功能前感知可用性
- API 错误响应增加 `code` 字段，区分 `LLM_NOT_CONFIGURED`、`LLM_AUTH_FAILED`、`LLM_RATE_LIMITED`、`LLM_PROVIDER_ERROR` 等错误类型
- Explore 页面在 LLM 未配置时显示引导卡片，替代隐藏在 console 中的错误信息
- Explore 页面 stream error 根据 error code 展示不同的友好提示

## Capabilities

### New Capabilities

- `llm-status`: LLM 可用性状态查询——status API 暴露 LLM 配置状态，前端可提前感知 LLM 是否就绪
- `llm-error-classification`: LLM 错误分类——API 层将 LLM 相关错误统一分类为结构化错误码，前端按类型展示不同引导

### Modified Capabilities

- `http-api`: status 端点增加 `llm` 字段；错误响应增加 `code` 字段
- `web-ui`: Explore 页面增加 LLM 未配置时的引导卡片和分类错误提示

## Impact

- `packages/cli/src/api/status.ts` — 增加 LLM 状态查询逻辑
- `packages/cli/src/api/explore.ts` — 错误响应增加 code 字段
- `packages/cli/src/agent-runtime/llm.ts` — 错误类型可识别（抛出带 code 的错误）
- `packages/cli/web/src/components/ExplorePage.tsx` — LLM 状态检查 + 引导 UI
- `packages/cli/web/src/hooks/useExploreStream.ts` — 解析 code 字段
- `packages/cli/web/src/lib/api.ts` — 可能需要增加 status 查询
