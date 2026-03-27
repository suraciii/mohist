## Context

Mohist 是一个 AI 驱动的工作流工具，目标是将"想法到代码"变成一条流水线。当前实现基于确定性状态机：阶段转换、agent prompt、错误处理全部硬编码在 `workflow/` 和 `agent/` 目录中。

现有代码约 60% 可复用：db/（SQLite + repos）、git/（WorktreeManager）、server/（Express HTTP）、api/（REST routes）、cli/（Commander CLI）、providers/（local issue provider）。约 40% 需要替换：types/（硬编码 Stage 枚举）、workflow/（状态机 + handlers + engine）、agent/（静态 prompt + runner）。

参考架构：openclaw（agent 框架模式：长 session + sub-agent spawning + gate pause/resume）、opencode（task tool：sub-agent 通过 Session.create + SessionPrompt.prompt 实现同步等待）。

技术栈：Node.js + TypeScript + better-sqlite3 + Express。新增 Vercel AI SDK v5（`ai` package）+ provider packages（`@ai-sdk/anthropic`、`@ai-sdk/openai` 等）+ Zod。

## Milestone 策略

整个改造分为 4 个 Milestone，**本次只做 Milestone 1**。

```
Milestone 1 (本次): 最小可跑通
  单 issue，纯自动，LLM 编排，1 个 sub-agent (Code)
  不含 Event Bus、attach、workflow.yaml、gate 暂停

Milestone 2 (后续): 能交互
  Event Bus + ask_user + mo attach + gate 暂停/恢复

Milestone 3 (后续): 能配置
  workflow.yaml + 完整 4 sub-agent (Explore/Plan/Code/Verify)

Milestone 4 (后续): 收尾
  Rollback + mo status + 清理旧代码
```

## Goals / Non-Goals

**Milestone 1 Goals:**
- Main Agent（LLM 编排器）能自动完成一个 issue 的完整生命周期
- 两层 agent 架构落地：Main Agent（编排）+ Code Agent（执行）
- Sub-agent 通过 spawn_agent 工具 spawn opencode 子进程
- Session 纯内存管理（M1 不持久化到 SQLite）
- mo issue create → mo issue start → 自动完成

**Milestone 1 Non-Goals:**
- 不实现 Event Bus（后续 Milestone 2）
- 不实现 ask_user / mo attach / gate 暂停（后续 Milestone 2）
- 不实现 workflow.yaml（后续 Milestone 3，阶段硬编码在 system prompt）
- 不实现 Explore/Plan/Verify sub-agent（后续 Milestone 3，只有 Code Agent）
- 不实现多 issue 并发（先单 issue）
- 不实现 session 恢复（server 重启 = issue 重新 start）
- 不实现 GitHub provider（仅 local provider）
- 不实现 Web UI

**Overall Non-Goals:**
- 不实现多 issue 并行的 worker pool
- 不实现 sub-agent 的 compaction（session 生命周期短，不会爆 context）
- 不实现 opencode 的完整工具集（只实现 Mohist 需要的子集）
- 不实现 workflow.yaml 的条件分支、并行阶段

## Decisions

### D1: 两层 Agent 架构

**选择**: Main Agent（编排器, per-issue session）+ Sub-Agents（执行者, 用完即弃）

Main Agent 不直接操作代码，负责读取流程定义、编排 sub-agent、评估产出、推进阶段。Sub-Agent 是执行者，通过 spawn opencode 子进程完成具体工作。

**Milestone 1 简化**: 只有 Code Agent 一个 sub-agent，阶段硬编码在 Main Agent 的 system prompt 里。

**替代方案**:
- A: 所有智能都在 opencode，Mohist 只是调度器 → 否决：Mohist 需要 explore/plan/verify 等非编码智能
- B: Mohist 造完整 agent framework → 否决：过度工程，sub-agent 是一次性 session 不需要复杂 runtime

### D2: Sub-Agent 执行方式

**选择**: Main Agent 通过 `spawn_agent` 工具 spawn opencode 子进程。M1 内部直接 spawn opencode（无子 LLM loop），M3 升级为子 LLM loop。

**M1 实现**: `spawn_agent(agent_type, prompt, cwd)` 内部等价于 `spawn opencode --message prompt`。opencode 子进程在 issue 的 worktree 中执行，Main Agent 同步等待 stdout/stderr/exit_code。

**M3 升级**: `spawn_agent` 改为创建子 LLM loop，子 agent 有独立的 system prompt + 工具集。Code Agent 的工具集包含 `call_opencode`。M1→M3 的升级对 Main Agent 完全透明。

**替代方案**:
- A: Main Agent 直接用 call_opencode 工具（无 spawn_agent 抽象）→ 否决：M3 加子 LLM loop 时需要改 Main Agent 工具集
- B: M1 就实现子 LLM loop → 否决：每个阶段多一次完整 LLM 调用，M1 成本和复杂度翻倍，不符合"最小可跑通"

**理由**: opencode 已有完整的代码编辑、文件读写、bash 执行能力，不需要在 Mohist 侧重新实现。`spawn_agent` 是零成本抽象 — M1 内部是透传，M3 升级时 Main Agent 无感知。

### D3: LLM — Vercel AI SDK v5

**选择**: 使用 Vercel AI SDK v5 的 `streamText()` 实现 LLM tool loop。AI SDK 内置 tool calling cycle（LLM 返回 tool_call → 自动执行 → 结果喂回 → 继续生成）。

**Milestone 1 简化**: 统一使用一个模型（Sonnet），不折腾多模型配置。Main Agent 和 Code Agent 用同一个 provider。

**理由**: 和 opencode 技术栈一致，自动支持主流 provider（Anthropic、OpenAI、Google 等），内置 tool calling cycle 不需要自己实现。

#### LLM Provider 配置（参考 opencode）

参考 opencode 的 `provider.ts` 实现模式，M1 做简化版。

**Opencode 的核心模式**（Mohist 复用）:
1. `config.json` 定义 provider options（apiKey, baseURL）
2. 环境变量自动检测 API key（provider.env 数组）
3. AI SDK 创建实例：`createAnthropic({ apiKey, baseURL }).languageModel("claude-sonnet-4")`

**Opencode 的复杂度**（M1 不需要）:
1. models.dev 在线数据库 — Mohist 不需要动态发现 provider
2. auth.json 持久化 — Mohist 不存 API key
3. plugin 系统 / 自定义 npm provider — Mohist 只用 bundled 的
4. 多 provider 并存 + disabled/enabled 过滤 — M1 只用一个

**M1 配置方案**:

```
配置来源（优先级从高到低）:
  1. ~/.mohist/config.json 里的 llm.provider.<id>.options
  2. 环境变量（复用 opencode 的，不单独设）

环境变量:
  ANTHROPIC_API_KEY=xxx       ← Mohist 直接复用，和 opencode 共享
  OPENAI_API_KEY=xxx          ← 同上

~/.mohist/config.json:
{
  "llm": {
    "model": "anthropic/claude-sonnet-4",   // "provider/model-id" 格式
    "provider": {
      "anthropic": {
        "options": {
          "baseURL": "http://127.0.0.1:20172"  // 可选，代理
        }
      }
    }
  }
}
```

**M1 初始化流程**（简化版 `getSDK` + `getLanguage`）:

```typescript
// agent-runtime/llm.ts
const PROVIDER_ENV: Record<string, string[]> = {
  anthropic: ["ANTHROPIC_API_KEY"],
  openai: ["OPENAI_API_KEY"],
}
const BUNDLED_NPM: Record<string, string> = {
  anthropic: "@ai-sdk/anthropic",
  openai: "@ai-sdk/openai",
}

function resolveModel(config): LanguageModel {
  const [providerID, modelID] = config.model.split("/")
  // 1. 从环境变量检测 API key
  const envKey = PROVIDER_ENV[providerID]?.map(e => process.env[e]).find(Boolean)
  // 2. 合并 config.json 里的 options
  const options = config.provider?.[providerID]?.options ?? {}
  if (envKey) options.apiKey = envKey
  // 3. 创建 SDK 实例（参考 opencode getSDK）
  const npm = BUNDLED_NPM[providerID]
  const sdk = BUNDLED[npm]({ name: providerID, ...options })
  // 4. 创建 language model（参考 opencode getLanguage）
  return sdk.languageModel(modelID)
}
```

**后续扩展**: M3 多模型时，可引入 models.dev 数据库做动态 provider 发现，完整参考 opencode 的 `Provider.state()` 实现。

### D4: workflow.yaml — Milestone 3 再实现

**选择**: Milestone 1 阶段定义硬编码在 Main Agent 的 system prompt 里。后续 Milestone 3 抽取为 workflow.yaml。

**Milestone 1 流程** (写死在 prompt 中):
```
design → implement → done
```

**后续设计** (D4 full): yaml 包含两类字段：
- 给 Code 读：`agent`（引用 agent 定义）、`gate_after`（approve/auto）
- 给 LLM 读：`description`（阶段描述）、`expects`（产出期望）

### D5: Event Bus — Milestone 2 再实现

**选择**: Milestone 1 不需要 Event Bus。组件间通过直接函数调用通信。

**后续设计** (D5 full): 内存 Event Bus（callback-based Set<listener>，参考 openclaw），组件间通过事件通信。

### D6: ask_user — Milestone 2 再实现

**选择**: Milestone 1 没有 gate 暂停，Agent 纯自动跑完。用户通过 `mo issue approve` 推进（如果有的话）。

**后续设计** (D6 full): Deferred + Pending Map，参考 opencode Question 模块。

### D7: 用户消息注入 — Milestone 2 再实现

**选择**: Milestone 1 不支持自由文本注入。所有交互通过 CLI 命令（start/approve）。

**后续设计** (D7 full): ask_user 和 attach 对话统一为消息注入。

### D8: Session 管理

**选择**: M1 纯内存 session（Layer 1）。SessionManager 管理 Map<sessionId, Session>，不涉及 SQLite。sub-agent 无 session（用完即弃）。

**M1 简化**: 不实现 session 持久化、不实现 session 恢复。server 重启后，running 状态的 issue 标记为 failed，需要重新 start。LLM loop 的 messages 只存在于内存中。

**M2 升级**: session 持久化是 ask_user + gate 暂停的前置条件。M2 时升级为 Layer 2（appendMessage 同时写 SQLite，但不恢复）→ Layer 3（支持从 SQLite 恢复 session）。

**理由**: M1 目标是跑通。纯内存 session 最简单（~50 行），调试时直接 console.log。持久化的 ROI 在 M1 很低 — 用户通过看 worktree 文件就能知道 agent 做了什么。M2 需要 gate 暂停时，自然需要持久化，那时升级路径清晰。

### D9: 进度追踪 — Milestone 4 再实现

**选择**: Milestone 1 继续使用 issues.stage 字段作为进度标识。mo issue show 查 stage 即可。

**后续设计** (D9 full): `workflow_log` 表，append-only，用于 `mo status` 的时间线展示。

### D10: 回退机制 — Milestone 4 再实现

**选择**: Milestone 1 不支持回退。

**后续设计** (D10 full): 用户发送回退命令，Main Agent LLM 决策处理。

### D11: 迁移策略 — 新目录共存

**选择**: 在 `src/agent-runtime/` 新目录中开发核心能力（LLM loop、session、tools），旧 `workflow/` 和 `agent/` 代码保留，直到新系统跑通后统一删除。每一步保持可编译。

**理由**: 新旧代码可以共存，避免中间状态不可编译。旧代码在 Phase 1 期间不会被启动路径引用，但文件保留以便参考。

### D12: 并发模型 — 先单 Issue

**选择**: Milestone 1 一次只处理一个 issue。start 端点在当前有 issue 运行时返回错误。

**后续**: per-issue 独立 session + LLM loop，支持多 issue 并发。

## Risks / Trade-offs

**[Main Agent LLM 决策质量]** → Main Agent 的决策质量取决于 LLM 能力。错误决策可能导致阶段跳过或重复执行。Mitigation: Milestone 1 纯自动，出错可重新 start。后续 Milestone 加 gate 硬控制。

**[Sub-Agent Prompt 质量]** → sub-agent 的行为完全由 prompt 决定，prompt 不当可能导致产出不符合 expects。Mitigation: Main Agent 在 advance 前评估产出是否满足 expects，不满足则重试。

**[LLM 成本]** → 每个 issue 需要多次 LLM 调用（Main Agent + Code Agent）。Milestone 1 统一用 Sonnet，成本可控。

**[Opencode 子进程超时]** → Code Agent 调用 opencode 可能长时间不退出。Mitigation: 可配置的 timeout（默认 30 分钟），超时后 abort 子进程。复用现有 agent/runner.ts 的 spawn 逻辑。

**[新旧代码并存期复杂度]** → agent-runtime/ 和 workflow/agent/ 共存期间，可能造成困惑。Mitigation: 旧代码在启动路径中被替换后立即删除，不长期保留。

## Implementation Plan

### 目录结构变化

```
src/
├── agent-runtime/          ← 新增
│   ├── llm.ts              ← AI SDK streamText() 封装
│   ├── tool.ts             ← Tool.define + ToolRegistry
│   ├── session.ts          ← Session + SessionManager (纯内存)
│   ├── agent-loop.ts       ← LLM tool loop
│   └── index.ts
│
├── agents/                 ← 新增
│   ├── main-agent.ts       ← Main Agent (system prompt + 编排)
│   ├── code-agent.ts       ← Code Agent 定义 (prompt 模板, 非独立 LLM loop)
│   └── index.ts
│
├── tools/                  ← 新增
│   ├── spawn-agent.ts      ← spawn opencode 子进程 (M1 透传, M3 升级为子 LLM loop)
│   ├── advance-stage.ts    ← 推进阶段
│   ├── add-comment.ts      ← 添加评论
│   ├── get-issue.ts        ← 读取 issue 信息
│   └── index.ts
│
├── workflow/               ← 保留到迁移完成，最后删除
├── agent/                  ← 保留到迁移完成，最后删除
├── types/index.ts          ← 修改: Stage 枚举扩展
├── services/               ← 修改: workflow-service.ts 简化
├── api/issues.ts           ← 修改: start 端点调新 agent
├── server/                 ← 保留
├── cli/                    ← 保留
└── git/worktree-manager.ts ← 保留
```

### 迁移序列（每步可编译）

```
Step 1: 加依赖
  npm install ai @ai-sdk/anthropic zod
  验证: npm run build 通过

Step 2: 新增 agent-runtime/llm.ts
  封装 AI SDK streamText()，provider 从环境变量读
  验证: 单元测试能调一次 LLM

Step 3: 新增 agent-runtime/tool.ts
  Tool.define(id, { description, parameters: zodSchema, execute })
  ToolRegistry.register / get
  验证: 定义 echo tool，注册，调用

Step 4: 新增 agent-runtime/session.ts (纯内存)
  SessionManager: create, appendMessage, getMessages, close
  不涉及 SQLite，纯 Map<id, Session>
  验证: 创建 session，写入/读取消息

Step 5: 新增 agent-runtime/agent-loop.ts
  runAgentLoop(session, tools, model, options)
  核心: streamText() + maxSteps + tool calling cycle
  验证: echo tool 的完整 loop 跑通

Step 6: 新增 tools/ (spawn-agent, advance-stage, add-comment, get-issue)
  spawn_agent: spawn opencode --message prompt (复用 agent/runner.ts 逻辑)
  advance_stage: 更新 issues.stage
  add_comment: 插入 comments 表
  get_issue: 查询 issues 表
  验证: tool 调用后数据库正确更新

Step 7: 新增 agents/code-agent.ts + agents/main-agent.ts
  code-agent.ts: Agent 定义 (name, prompt 模板)，M1 不是独立 LLM loop
  main-agent.ts: system prompt (硬编码 design → implement → done) + 工具集
  验证: Main Agent 能调 spawn_agent 跑 opencode 并返回结果

Step 8: 修改 api/issues.ts start 端点
  start 不再创建 Task + 交给 WorkflowEngine
  改为: 创建 session → 启动 Main Agent loop
  验证: mo issue start 触发 Agent 运行

Step 9: 修改 types/index.ts
  Stage 枚举扩展，兼容旧代码
  验证: npm run build 通过

Step 10: 端到端验证
  mo issue create "写个 hello world"
  mo issue start
  观察: Main Agent 调 spawn_agent → opencode 执行 → 完成

Step 11: 清理旧代码
  删除 workflow/engine.ts, workflow/issue-workflow.ts,
  workflow/stage-handlers.ts, agent/runner.ts, agent/prompts.ts
  验证: npm run build 通过
```

### 第一版 Main Agent 流程

```
mo issue start 1
    │
    ▼
┌─ Main Agent (唯一的 LLM loop) ───────────────────┐
│                                                    │
│  System prompt:                                    │
│    "你是 Mohist 工作流编排器。当前 issue: #{1}"     │
│    "流程: design → implement → done"               │
│    "每个阶段完成后调用 advance_stage"               │
│                                                    │
│  1. 调 spawn_agent("code", "根据 issue 设计方案")  │
│     → 内部: spawn opencode --message "..."         │
│     → 同步等待退出，返回 stdout/stderr/exit_code   │
│     → 返回: design.md 已创建                       │
│                                                    │
│  2. 调 advance_stage("implement")                  │
│     → issues.stage = "implement"                   │
│                                                    │
│  3. 调 spawn_agent("code", "按照 design.md 实现")  │
│     → 内部: spawn opencode --message "..."         │
│     → 同步等待退出，返回 stdout/stderr/exit_code   │
│     → 返回: 代码已实现                             │
│                                                    │
│  4. 调 advance_stage("done")                       │
│     → issues.stage = "done"                        │
│                                                    │
│  5. LLM loop 结束                                  │
└────────────────────────────────────────────────────┘

M1: spawn_agent 内部直接 spawn opencode（无子 LLM loop）
M3: spawn_agent 升级为创建子 LLM loop，Main Agent 无感知
```

第一版没有 gate 暂停，Agent 纯自动跑完。这在 Milestone 2 加 ask_user 后才引入。

### 关键设计约束

- C1: 不依赖 Event Bus（Milestone 2 再加）
- C2: 不依赖 workflow.yaml（Milestone 3 再加）
- C3: 不需要 session 持久化（纯内存，Milestone 2 再加 SQLite）
- C4: 不需要 session 恢复（server 重启 = issue 重新 start）
- C5: 单 issue（不需要并发控制）
- C6: Stage 枚举暂时保留，和旧代码兼容
- C7: spawn_agent M1 直接透传到 opencode 子进程，不经过子 LLM loop

## Open Questions

Milestone 1 需要回答:
- Main Agent system prompt 具体内容（M1 实现时迭代，不需要设计阶段定死）

已解决 (2026-03-28 explore):
- ~~Session 持久化~~ → M1 纯内存 session (D8 更新)
- ~~spawn_agent vs call_opencode~~ → spawn_agent 工具，M1 透传到 opencode (D2 更新)
- ~~agents/code-agent.ts 定位~~ → M1 是 agent 定义文件（prompt 模板），不是独立 LLM loop
- ~~LLM provider 配置~~ → 参考 opencode 模式，config.json + 环境变量 (D3 更新)
- ~~spawn_agent 参数~~ → { agent_type, task, cwd?, timeout? }，task 由 Main Agent LLM 构建
- ~~start 端点切换~~ → Option A 直接替换，engine 整个不需要启动

后续 Milestone 需要回答:
- workflow.yaml 完整 schema（D4 full）
- Event Bus 事件类型定义（D5 full）
- ask_user 的 Deferred 实现细节（D6 full）
- mo attach 连接协议：HTTP polling vs SSE（D7 full）
- Session 恢复流程（D8 full）
- workflow_log 事件类型定义（D9 full）
- 回退命令解析和冲突处理（D10 full）
- 多 issue 并发的 session 隔离和资源管理（D12 后续）
