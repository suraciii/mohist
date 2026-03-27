## Context

Mohist 是一个 AI 驱动的工作流工具，目标是将"想法到代码"变成一条流水线。当前实现基于确定性状态机：阶段转换、agent prompt、错误处理全部硬编码在 `workflow/` 和 `agent/` 目录中。

现有代码约 60% 可复用：db/（SQLite + repos）、git/（WorktreeManager）、server/（Express HTTP）、api/（REST routes）、cli/（Commander CLI）、providers/（local issue provider）。约 40% 需要替换：types/（硬编码 Stage 枚举）、workflow/（状态机 + handlers + engine）、agent/（静态 prompt + runner）。

参考架构：openclaw（agent 框架模式：长 session + sub-agent spawning + gate pause/resume）、opencode（task tool：sub-agent 通过 Session.create + SessionPrompt.prompt 实现同步等待）。

技术栈：Node.js + TypeScript + better-sqlite3 + Express。新增 Vercel AI SDK v5（`ai` package）+ provider packages（`@ai-sdk/anthropic`、`@ai-sdk/openai` 等）+ Zod。

## Goals / Non-Goals

**Goals:**
- Mohist 成为一个拥有自己 LLM 的 AI agent，能理解上下文、做出决策
- 实现两层 agent 架构：Workflow Agent（编排器）+ Sub-Agents（执行者）
- workflow.yaml 驱动流程定义，支持 per-project 自定义
- Event-driven 交互，支持多 channel（CLI/WeChat/Telegram）
- 用户通过 approve gate 控制关键阶段转换
- Sub-agent 用完即弃，Main Agent session 持久化

**Non-Goals:**
- 不实现多 issue 并行的 worker pool（per-issue 串行执行）
- 不实现 sub-agent 的 compaction（session 生命周期短，不会爆 context）
- 不实现 opencode 的完整工具集（只实现 Mohist 需要的子集）
- 不实现 GitHub provider（仅 local provider）
- 不实现 Web UI（先 CLI）
- 不实现 workflow.yaml 的条件分支、并行阶段

## Decisions

### D1: 两层 Agent 架构

**选择**: Workflow Agent（Main Agent, long session）+ Sub-Agents（short-lived session）

Main Agent 是编排器，per-issue 一个持久化 session，不直接操作代码。Sub-Agents 是执行者，每个阶段 spawn 一个，独立 LLM loop + 工具集，用完即弃。

**参考**: opencode 的 task tool 模式（Session.create → SessionPrompt.prompt → 提取文本 → 返回结果）。

**替代方案**:
- A: 所有智能都在 opencode，Mohist 只是调度器 → 否决：Mohist 需要 explore/plan/verify 等非编码智能
- B: Mohist 造完整 agent framework → 否决：过度工程，sub-agent 是一次性 session 不需要复杂 runtime

### D2: Sub-Agent Spawning — 进程内

**选择**: Sub-agent 在 Mohist server 进程内运行，通过 Vercel AI SDK `streamText()` + maxSteps 实现 LLM tool loop。

Code Agent 例外：通过 spawn opencode 子进程执行编码任务（`call_opencode` 工具）。

**替代方案**:
- A: 每个 sub-agent 一个子进程 → 否决：进程间通信复杂，Mohist 的 sub-agent 是轻量的一次性 session
- B: 所有 sub-agent 都用 opencode 子进程 → 否决：explore/plan/verify 不需要 opencode 的完整能力

### D3: LLM — Vercel AI SDK v5

**选择**: 使用 Vercel AI SDK v5 的 `streamText()` 实现 LLM tool loop。AI SDK 内置 tool calling cycle（LLM 返回 tool_call → 自动执行 → 结果喂回 → 继续生成）。

**理由**: 和 opencode 技术栈一致，自动支持主流 provider（Anthropic、OpenAI、Google 等），内置 tool calling cycle 不需要自己实现。

### D4: workflow.yaml — 结构性 + 语义性字段

**选择**: yaml 包含两类字段：
- 给 Code 读：`agent`（引用 agent 定义）、`gate_after`（approve/auto）
- 给 LLM 读：`description`（阶段描述）、`expects`（产出期望）

不包含：agent prompt、agent 工具集、agent 模型、entry_condition、type 字段。这些在代码里定义。

**理由**: yaml 的读者是 LLM 和开发者，不是终端用户。agent 行为（prompt + 工具 + 模型）是代码职责，不是配置职责。

### D5: Event-Driven 交互

**选择**: 内存 Event Bus（callback-based Set<listener>，参考 openclaw），组件间通过事件通信。

Channel（CLI/WeChat/Telegram）作为 Bus 的 consumer，接收事件并渲染给用户。用户输入通过 Channel 注入 Bus，路由到对应的 agent session。

**替代方案**:
- A: 直接函数调用，不走 Bus → 否决：多 channel 需要广播能力，直接调用无法解耦
- B: 消息队列（Redis/RabbitMQ）→ 否决：Mohist 是单进程，不需要跨进程通信

### D6: ask_user — Deferred + Pending Map

**选择**: 参考 opencode 的 Question 模块。ask_user 工具创建 Deferred，存入 pending map，await 阻塞 agent loop。Bus 广播 `question.asked` 事件，Channel 渲染问题。用户回答后 Bus 广播 `question.replied`，resolve Deferred，agent loop 继续。

**理由**: AI SDK 的 streamText() 在等待 tool result 时自然阻塞整个 agent loop，不需要显式的 pause/resume 机制。

### D7: 用户消息 vs ask_user — 统一为消息注入

**选择**: 两种交互在底层都是往 session 里注入 user message：
- ask_user（agent 主动问）→ 工具返回 Promise 阻塞 → 用户回答后 resolve
- 用户主动说（attach 对话）→ 直接注入 session → 触发 LLM 新一轮

**冲突处理**: 如果 sub-agent 被 ask_user 阻塞时用户发自由文本，Main Agent 的 LLM 决策如何处理（cancel sub-agent / 排队 / 忽略）。

### D8: Session 持久化 — 完整历史存储

**选择**: Main Agent session 的完整 messages[] 存储在 SQLite。sub-agent session 不持久化（用完即弃）。

暂不实现 compaction。典型 issue 生命周期约 30-100 条 messages，在主流模型 context window（128k+）内完全够用。

### D9: 进度追踪 — Append-only Log

**选择**: `workflow_log` 表，append-only，记录所有 workflow 事件（stage_enter/exit、agent_spawn/done、decision、human_action）。用于进度展示（`mo status`）和审计。

issues 表保留 `stage` 字段作为可变状态（方便查询），不采用 event-sourced 模式（状态从事件流推导）。

### D10: 回退机制 — 用户显式命令

**选择**: 用户通过 channel 发送回退命令（如"回到探索"），Main Agent LLM 决策处理：
1. cancel 当前 sub-agent（abort）
2. 更新 issues.stage
3. append workflow_log rollback 事件
4. 重新 spawn 目标阶段的 sub-agent

已有产出（代码、文件）保留不删除（git 记录一切），新 sub-agent 可以参考。

## Risks / Trade-offs

**[Main Agent LLM 质量风险]** → Main Agent 的决策质量取决于 LLM 能力。错误决策可能导致阶段跳过或重复执行。Mitigation: gate_after=approve 的硬 gate 由代码控制，关键转换必须人工确认。

**[Sub-Agent Prompt 质量]** → sub-agent 的行为完全由 prompt 决定，prompt 不当可能导致产出不符合 expects。Mitigation: Main Agent 在 advance 前评估产出是否满足 expects，不满足则重试。

**[LLM 成本]** → 每个 issue 需要多次 LLM 调用（Main Agent + 各 sub-agent）。Mitigation: Main Agent 用便宜快速的模型（haiku），只有 Code Agent 用强模型。

**[Event Bus 内存泄漏]** → Bus listener 如果没有正确清理，可能导致内存泄漏。Mitigation: sub-agent 结束时清理所有 listener，使用 WeakRef 或显式 unsubscribe。

**[ask_user 无 Channel 在线]** → 如果没有 channel 连接，ask_user 会永久阻塞。Mitigation: Main Agent 在 spawn 需要 ask_user 的 sub-agent 前，检查是否有在线 channel。无 channel 时跳过或使用默认行为。

**[Opencode 子进程超时]** → Code Agent 调用 opencode 可能长时间不退出。Mitigation: 可配置的 timeout（默认 30 分钟），超时后 abort 子进程。

## Open Questions

- Main Agent system prompt 的具体内容和格式
- 每个 sub-agent 的详细 prompt 设计（Explore/Plan/Code/Verify）
- Code Agent 的 `call_opencode` 工具具体实现：传递什么参数、怎么捕获输出、怎么处理错误
- Mohist 的 LLM provider 配置文件格式（`~/.mohist/config.json` 的结构）
- `mo attach 42` 的连接协议：HTTP polling vs WebSocket vs SSE
- 多 channel 同时在线时的消息去重和优先级
- Server 重启后 Main Agent session 的恢复流程
