## Why

Mohist 当前的 workflow engine 是一个确定性状态机 — 阶段转换、agent prompt、错误处理全部硬编码。但产品需求是"智能推进"：AI 需要判断需求是否聊清楚、方案是否合理、代码质量是否合格，而不是机械地按固定规则流转。确定性的 engine 无法满足这个需求。

需要将 Mohist 从"确定性引擎"转变为"AI Agent" — Mohist 本身是一个拥有 LLM 的 agent，能理解上下文、做出决策、编排子 agent 执行具体工作。

## Milestone Strategy

本 change 分 4 个 Milestone 实现，详见 [design.md](design.md#milestone-策略)。

- **M1 (本次)**: 最小可跑通 — 单 issue、纯自动、1 sub-agent (Code)、无 Event Bus、无 gate
- **M2**: 能交互 — Event Bus + ask_user + mo attach + gate 暂停/恢复
- **M3**: 能配置 — workflow.yaml + 完整 4 sub-agent + spawn_agent 升级为子 LLM loop
- **M4**: 收尾 — Rollback + mo status + session 持久化 + 清理旧代码

## What Changes (M1)

- **BREAKING**: 移除当前确定性 workflow engine（WorkflowEngine、StageHandlers、硬编码状态转换）
- **BREAKING**: 移除当前的 AgentRunner 和静态 prompt 模板
- **新增**: Mohist Agent Runtime — 基于 Vercel AI SDK v5 的 LLM tool loop，支持内存 session 管理和 spawn_agent（opencode 子进程透传）
- **新增**: Main Agent（Workflow Agent）— per-issue 的内存 session，硬编码 design → implement → done 流程，编排 opencode 子进程执行具体工作
- **新增**: LLM Provider 配置 — 参考 opencode 模式，config.json + 环境变量（复用 ANTHROPIC_API_KEY）
- **修改**: start 端点从 WorkflowEngine 切换为 Main Agent loop
- **保留**: db/、git/、server/、api/、cli/ 框架、providers/（约 60% 现有代码）

## Capabilities

### New Capabilities

- `agent-runtime`: Mohist Agent Runtime — LLM tool loop (streamText + maxSteps)、tool system (Zod)、内存 session 管理、LLM provider 配置。基于 Vercel AI SDK v5，参考 opencode provider.ts 的 getSDK + getLanguage 模式。
- `workflow-agent`: Main Agent — per-issue 内存 session，硬编码 design → implement → done 流程，通过 spawn_agent 工具编排 opencode 子进程，评估产出，调用 advance_stage 推进阶段。

### Removed Capabilities

- `workflow-engine`: 移除确定性 engine（engine.ts、issue-workflow.ts、stage-handlers.ts）
- `agent-runner`: 移除 AgentRunner 和静态 prompt 模板（runner.ts、prompts.ts）

## Impact

- **代码**: 删除 `workflow/` 核心文件，删除 `agent/` 核心文件，新增 `agent-runtime/`、`agents/`、`tools/` 目录
- **依赖**: 新增 `ai` (Vercel AI SDK v5)、`@ai-sdk/anthropic`、`zod`
- **存储**: M1 无新增表（session 纯内存）
- **配置**: Config 表新增 llm 配置项（model、provider options）
- **进程模型**: Mohist server 内运行一个 Main Agent LLM loop + opencode 子进程
