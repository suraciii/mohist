## Why

Kimi、智谱 GLM、MiniMax 三家国产 LLM 供应商提供了 "Coding Plan" 订阅模式，用户通过订阅获得免费额度使用其编程模型。这些 coding plan 使用**专用 API 端点**（不同于普通 API），且 Kimi 和 MiniMax 的 coding plan 采用 **Anthropic Messages API 协议**（而非 OpenAI Chat Completion）。mohist 当前的内置 provider 只注册了普通 API 端点，无法使用这些订阅制的 coding plan 服务。

## What Changes

- 新增 3 个内置 coding plan provider：`zhipuai-coding-plan`、`kimi-for-coding`、`minimax-for-coding`
- `kimi-for-coding` 和 `minimax-for-coding` 使用 `anthropic` SDK（因为它们的 coding plan 端点兼容 Anthropic Messages API）
- `zhipuai-coding-plan` 使用 `openai-compatible` SDK，端点为 `https://open.bigmodel.cn/api/coding/paas/v4`

## Capabilities

### New Capabilities

（无新 capability，coding plan provider 是已有 provider 系统的扩展）

### Modified Capabilities

- `provider-config`: 新增 3 个 coding plan 内置 provider 注册表条目
- `provider-registry`: 新增 coding plan provider 的动态解析场景

## Impact

- `packages/cli/src/config/builtin-providers.ts` — 新增 3 个 provider 定义
- `openspec/specs/provider-config/spec.md` — 更新内置 provider 注册表需求
- `openspec/specs/provider-registry/spec.md` — 新增 coding plan 解析场景
