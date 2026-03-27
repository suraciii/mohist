## Why

Mohist 当前的 workflow engine 是一个确定性状态机 — 阶段转换、agent prompt、错误处理全部硬编码。但产品需求是"智能推进"：AI 需要判断需求是否聊清楚、方案是否合理、代码质量是否合格，而不是机械地按固定规则流转。确定性的 engine 无法满足这个需求。

需要将 Mohist 从"确定性引擎"转变为"AI Agent" — Mohist 本身是一个拥有 LLM 的 agent，能读取 workflow 定义、理解上下文、做出决策、编排子 agent 执行具体工作。

## What Changes

- **BREAKING**: 移除当前确定性 workflow engine（WorkflowEngine、StageHandlers、硬编码状态转换）
- **BREAKING**: 移除当前的 AgentRunner 和静态 prompt 模板
- **新增**: Mohist Agent Runtime — 基于 Vercel AI SDK v5 的 LLM tool loop，支持 session 管理和 sub-agent spawning
- **新增**: Workflow Agent（Main Agent）— per-issue 的持久化 session，负责读取 workflow.yaml、编排 sub-agent、评估产出、处理用户交互
- **新增**: Sub-Agent 系统 — Explore、Plan、Code、Verify 四个 sub-agent，各自独立的 LLM loop + 工具集，用完即弃
- **新增**: workflow.yaml 驱动 — 流程定义从硬编码迁移到 `.mohist/workflow.yaml`，Main Agent 读取并理解
- **新增**: Event-driven 交互 — 基于 Bus 的事件系统，支持 CLI / WeChat / Telegram 多 channel 接入
- **新增**: ask_user 机制 — 基于 Deferred 的同步问答工具，sub-agent 可以主动向用户提问
- **修改**: issues 表增加 session 关联，新增 session 和 session_messages 表
- **修改**: CLI 增加 `attach` 命令，支持实时交互
- **保留**: db/、git/、server/、api/、cli/ 框架、providers/（约 60% 现有代码）

## Capabilities

### New Capabilities

- `agent-runtime`: Mohist Agent Runtime 核心能力 — LLM tool loop、session 管理、sub-agent spawning、tool system。基于 Vercel AI SDK v5 实现，参考 opencode 的 SessionPrompt.loop() 和 task tool 模式。
- `workflow-agent`: Workflow Agent（Main Agent）— per-issue 的持久化 session，读取 workflow.yaml，编排 sub-agent，评估产出质量，决定推进/重试/等待/回退，处理用户交互。不直接操作代码。
- `sub-agents`: Sub-Agent 系统定义 — Explore Agent（需求探索+用户对话）、Plan Agent（代码分析+方案输出）、Code Agent（调用 opencode 写代码）、Verify Agent（测试+审查）。每个 sub-agent 有独立的 prompt、工具集、模型配置。
- `workflow-config`: workflow.yaml 配置格式 — 阶段定义（agent、description、expects、gate_after），per-project 位于 `.mohist/workflow.yaml`。yaml 面向两个读者：Code（gate_after）和 LLM（description、expects）。
- `event-bus`: Event-driven 交互系统 — 内存事件总线，支持 producer/consumer 模式。事件类型包括：agent lifecycle、用户消息、状态变更。Channel 层（CLI/WeChat/Telegram）作为 consumer 接入。
- `user-interaction`: 用户交互能力 — ask_user 工具（Deferred + pending map，参考 opencode Question 模块）、mo attach 实时对话（自由文本注入 session）、approve/rollback 命令处理。支持多 channel 同时在线。

### Modified Capabilities

- `workflow-engine`: 移除当前确定性 engine，替换为 agent-driven workflow。状态转换从硬编码改为 LLM 决策 + gate 暂停。
- `agent-runner`: 移除当前 AgentRunner 和静态 prompt 模板，替换为 sub-agent spawning 系统。
- `issue-workflow`: 移除硬编码 STAGE_TRANSITIONS，阶段定义从 types/index.ts 迁移到 workflow.yaml。
- `cli-interface`: 新增 `attach` 命令，支持实时连接到 issue session。
- `http-api`: 新增 question 相关端点（list/reply/reject），channel 注册端点。
- `progress-reporting`: 从当前基于 stage 状态的进度改为基于 append-only workflow log 的事件驱动进度。

## Impact

- **代码**: `workflow/` 目录整体重写，`agent/` 目录重写，`types/index.ts` Stage 枚举修改，新增 `agent-runtime/` 目录
- **依赖**: 新增 `ai` (Vercel AI SDK v5)、`@ai-sdk/xxx` (provider packages)、`zod`
- **存储**: SQLite 新增 sessions、session_messages、workflow_log 表
- **配置**: 新增 `.mohist/workflow.yaml`、`~/.mohist/config.json`（LLM provider 配置）
- **进程模型**: Mohist server 内运行多个 LLM loop（Main Agent + sub-agents），需要考虑并发和资源管理
